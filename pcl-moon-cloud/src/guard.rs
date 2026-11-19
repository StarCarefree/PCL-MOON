use std::sync::OnceLock;

use axum::{
    body::Body,
    extract::{Request, State},
    http::{HeaderValue, Method, StatusCode, header},
    middleware::Next,
    response::{IntoResponse, Response},
};
use bytes::BytesMut;
use http_body_util::BodyExt;
use regex::Regex;
use subtle::ConstantTimeEq;

use crate::error::ApiError;
use crate::server::AppState;

pub const MAX_ADMIN_BODY: usize = 128 * 1024;

const CSP: &str = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' https: http:; connect-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";

fn public_pack_path_regex() -> &'static Regex {
    static RE: OnceLock<Regex> = OnceLock::new();
    RE.get_or_init(|| {
        Regex::new(r"^/api/v1/packs/[a-z0-9][a-z0-9_-]{0,63}(?:/download)?$").expect("valid regex")
    })
}

pub fn apply_security_headers(response: &mut Response) {
    let headers = response.headers_mut();
    headers.insert(
        header::X_CONTENT_TYPE_OPTIONS,
        HeaderValue::from_static("nosniff"),
    );
    headers.insert(header::X_FRAME_OPTIONS, HeaderValue::from_static("DENY"));
    headers.insert(
        header::REFERRER_POLICY,
        HeaderValue::from_static("no-referrer"),
    );
    headers.insert(
        header::CONTENT_SECURITY_POLICY,
        HeaderValue::from_static(CSP),
    );
}

fn is_read_method(method: &Method) -> bool {
    *method == Method::GET || *method == Method::HEAD
}

fn is_public_path(path: &str) -> bool {
    matches!(
        path,
        "/index.json" | "/catalog.json" | "/healthz" | "/api/v1/packs"
    ) || public_pack_path_regex().is_match(path)
}

fn strip_body_for_head(method: &Method, mut response: Response) -> Response {
    if *method == Method::HEAD {
        *response.body_mut() = Body::empty();
    }
    response
}

pub async fn public_guard(req: Request, next: Next) -> Response {
    let method = req.method().clone();
    let path = req.uri().path();

    let mut response = if !is_public_path(path) {
        ApiError::guard(StatusCode::NOT_FOUND, "资源不存在").into_response()
    } else if !is_read_method(&method) {
        ApiError::guard(StatusCode::METHOD_NOT_ALLOWED, "获取端口仅支持 GET/HEAD")
            .with_allow("GET, HEAD")
            .into_response()
    } else {
        next.run(req).await
    };

    response = strip_body_for_head(&method, response);
    apply_security_headers(&mut response);
    response
}

pub async fn admin_guard(State(state): State<AppState>, req: Request, next: Next) -> Response {
    let method = req.method().clone();
    let path = req.uri().path().to_string();
    let read = is_read_method(&method);

    let mut response = if read
        && matches!(
            path.as_str(),
            "/" | "/index.html" | "/app.js" | "/app.css" | "/healthz"
        ) {
        next.run(req).await
    } else if !path.starts_with("/api/admin/") {
        ApiError::guard(StatusCode::NOT_FOUND, "资源不存在").into_response()
    } else if !constant_time_auth(
        req.headers()
            .get(header::AUTHORIZATION)
            .and_then(|v| v.to_str().ok()),
        &state.expected_authorization,
    ) {
        ApiError::guard(StatusCode::UNAUTHORIZED, "管理令牌错误或缺失")
            .with_www_authenticate("Bearer")
            .into_response()
    } else if !read && !matches!(method, Method::POST | Method::PUT | Method::DELETE) {
        ApiError::guard(StatusCode::METHOD_NOT_ALLOWED, "请求方法不支持").into_response()
    } else if !read {
        match read_limited_body(req).await {
            Ok(buffered_req) => {
                let mut res = next.run(buffered_req).await;
                res.headers_mut()
                    .insert(header::CACHE_CONTROL, HeaderValue::from_static("no-store"));
                res
            }
            Err(err) => err.into_response(),
        }
    } else {
        let mut res = next.run(req).await;
        res.headers_mut()
            .insert(header::CACHE_CONTROL, HeaderValue::from_static("no-store"));
        res
    };

    response = strip_body_for_head(&method, response);
    apply_security_headers(&mut response);
    response
}

fn constant_time_auth(supplied: Option<&str>, expected: &[u8]) -> bool {
    let Some(supplied) = supplied else {
        return false;
    };
    let supplied_bytes = supplied.as_bytes();
    supplied_bytes.len() == expected.len() && bool::from(supplied_bytes.ct_eq(expected))
}

async fn read_limited_body(req: Request) -> Result<Request, ApiError> {
    if let Some(cl) = req
        .headers()
        .get(header::CONTENT_LENGTH)
        .and_then(|v| v.to_str().ok())
        .and_then(|s| s.trim().parse::<usize>().ok())
        && cl > MAX_ADMIN_BODY
    {
        return Err(ApiError::guard(
            StatusCode::PAYLOAD_TOO_LARGE,
            "请求体超过 128 KiB",
        ));
    }

    let (parts, mut body) = req.into_parts();
    let mut buf = BytesMut::new();
    while let Some(frame_res) = body.frame().await {
        let frame = frame_res.map_err(|_| {
            ApiError::new(
                StatusCode::BAD_REQUEST,
                "JSON 或参数格式错误，请检查字段与类型",
            )
        })?;
        if let Ok(data) = frame.into_data() {
            if buf.len().saturating_add(data.len()) > MAX_ADMIN_BODY {
                return Err(ApiError::guard(
                    StatusCode::PAYLOAD_TOO_LARGE,
                    "请求体超过 128 KiB",
                ));
            }
            buf.extend_from_slice(&data);
        }
    }

    Ok(Request::from_parts(parts, Body::from(buf.freeze())))
}
