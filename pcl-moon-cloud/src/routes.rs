use axum::{
    Json,
    body::Bytes,
    extract::{Path, Query, State},
    http::{HeaderMap, HeaderValue, Method, StatusCode, header},
    response::{IntoResponse, Response},
};
use serde::Serialize;

use crate::error::ApiError;
use crate::pack::Pack;
use crate::server::AppState;
use crate::store::AdminData;

const PUBLIC_CACHE: &str = "public, max-age=60, stale-while-revalidate=300";

#[derive(Serialize)]
struct HealthResponse {
    status: &'static str,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct StatusResponse {
    public_port: u16,
    admin_port: u16,
    pack_count: usize,
    published_count: usize,
    revision: i64,
}

#[derive(serde::Deserialize)]
pub struct DownloadQuery {
    mirror: Option<String>,
}

pub async fn healthz() -> impl IntoResponse {
    (
        StatusCode::OK,
        [(header::CACHE_CONTROL, "no-store")],
        Json(HealthResponse { status: "ok" }),
    )
}

pub async fn public_catalog(
    State(state): State<AppState>,
    method: Method,
    headers: HeaderMap,
) -> Response {
    state
        .store
        .current()
        .catalog
        .respond(&headers, &method, PUBLIC_CACHE)
}

pub async fn public_detail(
    State(state): State<AppState>,
    Path(id): Path<String>,
    method: Method,
    headers: HeaderMap,
) -> Result<Response, ApiError> {
    let snapshot = state.store.current();
    let blob = snapshot
        .details
        .get(&id)
        .ok_or_else(|| ApiError::new(StatusCode::NOT_FOUND, "整合包不存在或未发布"))?;
    Ok(blob.respond(&headers, &method, PUBLIC_CACHE))
}

pub async fn public_download(
    State(state): State<AppState>,
    Path(id): Path<String>,
    Query(query): Query<DownloadQuery>,
) -> Result<Response, ApiError> {
    let mirror: i32 = match query.mirror.as_deref() {
        None | Some("") => 0,
        Some(raw) => raw.parse::<i32>().map_err(|_| {
            ApiError::new(
                StatusCode::BAD_REQUEST,
                "JSON 或参数格式错误，请检查字段与类型",
            )
        })?,
    };

    let snapshot = state.store.current();
    let pack = snapshot
        .published
        .get(&id)
        .ok_or_else(|| ApiError::new(StatusCode::NOT_FOUND, "整合包不存在或未发布"))?;

    if mirror < 0 || mirror as usize > pack.backup_urls.len() {
        return Err(ApiError::new(StatusCode::BAD_REQUEST, "备用地址序号无效"));
    }

    let url = if mirror == 0 {
        &pack.download_url
    } else {
        &pack.backup_urls[(mirror as usize) - 1]
    };

    let location = HeaderValue::from_str(url)
        .map_err(|_| ApiError::new(StatusCode::BAD_REQUEST, "下载地址格式无效"))?;

    let mut response = Response::new(axum::body::Body::empty());
    *response.status_mut() = StatusCode::FOUND;
    response
        .headers_mut()
        .insert(header::CACHE_CONTROL, HeaderValue::from_static("no-store"));
    response.headers_mut().insert(header::LOCATION, location);
    Ok(response)
}

pub async fn admin_asset(
    State(state): State<AppState>,
    method: Method,
    headers: HeaderMap,
    uri: axum::http::Uri,
) -> Result<Response, ApiError> {
    state
        .assets
        .respond(uri.path(), &headers, &method)
        .ok_or_else(|| ApiError::guard(StatusCode::NOT_FOUND, "资源不存在"))
}

pub async fn admin_catalog(State(state): State<AppState>) -> Response {
    let snapshot = state.store.current();
    admin_response(&snapshot.admin)
}

pub async fn admin_create(
    State(state): State<AppState>,
    headers: HeaderMap,
    body: Bytes,
) -> Result<Response, ApiError> {
    let pack = parse_pack(&body)?;
    let expected = headers.get(header::IF_MATCH).and_then(|v| v.to_str().ok());
    let data = state.store.create(pack, expected)?;
    Ok(admin_response(&data))
}

pub async fn admin_update(
    State(state): State<AppState>,
    Path(id): Path<String>,
    headers: HeaderMap,
    body: Bytes,
) -> Result<Response, ApiError> {
    let pack = parse_pack(&body)?;
    let expected = headers.get(header::IF_MATCH).and_then(|v| v.to_str().ok());
    let data = state.store.update(&id, pack, expected)?;
    Ok(admin_response(&data))
}

pub async fn admin_delete(
    State(state): State<AppState>,
    Path(id): Path<String>,
    headers: HeaderMap,
) -> Result<Response, ApiError> {
    let expected = headers.get(header::IF_MATCH).and_then(|v| v.to_str().ok());
    let data = state.store.delete(&id, expected)?;
    Ok(admin_response(&data))
}

pub async fn admin_status(State(state): State<AppState>) -> impl IntoResponse {
    let snapshot = state.store.current();
    let body = StatusResponse {
        public_port: state.public_port,
        admin_port: state.admin_port,
        pack_count: snapshot.admin.packs.len(),
        published_count: snapshot.published.len(),
        revision: snapshot.admin.revision,
    };
    (
        StatusCode::OK,
        [(header::CACHE_CONTROL, "no-store")],
        Json(body),
    )
}

pub async fn admin_fallback() -> ApiError {
    ApiError::guard(StatusCode::NOT_FOUND, "资源不存在")
}

pub async fn admin_method_not_allowed() -> ApiError {
    ApiError::guard(StatusCode::METHOD_NOT_ALLOWED, "请求方法不支持")
}

fn parse_pack(body: &[u8]) -> Result<Pack, ApiError> {
    serde_json::from_slice(body).map_err(|_| {
        ApiError::new(
            StatusCode::BAD_REQUEST,
            "JSON 或参数格式错误，请检查字段与类型",
        )
    })
}

fn admin_response(data: &AdminData) -> Response {
    let mut response = Json(data).into_response();
    let headers = response.headers_mut();
    headers.insert(header::CACHE_CONTROL, HeaderValue::from_static("no-store"));
    if let Ok(etag) = HeaderValue::from_str(&format!("\"{}\"", data.revision)) {
        headers.insert(header::ETAG, etag);
    }
    response
}
