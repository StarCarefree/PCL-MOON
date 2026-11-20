use axum::{
    Json,
    http::{StatusCode, header},
    response::{IntoResponse, Response},
};
use serde::Serialize;

#[derive(Debug, Clone)]
pub struct ApiError {
    pub status: StatusCode,
    pub message: String,
    pub charset_utf8: bool,
    pub allow: Option<&'static str>,
    pub www_authenticate: Option<&'static str>,
}

#[derive(Serialize)]
struct ErrorBody<'a> {
    error: &'a str,
}

impl ApiError {
    pub fn new(status: StatusCode, message: impl Into<String>) -> Self {
        Self {
            status,
            message: message.into(),
            charset_utf8: false,
            allow: None,
            www_authenticate: None,
        }
    }

    pub fn guard(status: StatusCode, message: impl Into<String>) -> Self {
        Self {
            status,
            message: message.into(),
            charset_utf8: true,
            allow: None,
            www_authenticate: None,
        }
    }

    pub fn with_allow(mut self, allow: &'static str) -> Self {
        self.allow = Some(allow);
        self
    }

    pub fn with_www_authenticate(mut self, value: &'static str) -> Self {
        self.www_authenticate = Some(value);
        self
    }
}

impl IntoResponse for ApiError {
    fn into_response(self) -> Response {
        let mut response = (
            self.status,
            Json(ErrorBody {
                error: &self.message,
            }),
        )
            .into_response();
        let headers = response.headers_mut();
        headers.insert(
            header::CACHE_CONTROL,
            header::HeaderValue::from_static("no-store"),
        );
        if self.charset_utf8 {
            headers.insert(
                header::CONTENT_TYPE,
                header::HeaderValue::from_static("application/json;charset=UTF-8"),
            );
        }
        if let Some(allow) = self.allow {
            headers.insert(header::ALLOW, header::HeaderValue::from_static(allow));
        }
        if let Some(auth) = self.www_authenticate {
            headers.insert(
                header::WWW_AUTHENTICATE,
                header::HeaderValue::from_static(auth),
            );
        }
        response
    }
}
