use axum::{
    http::{HeaderMap, Method},
    response::Response,
};

use crate::blob::Blob;

const INDEX_HTML: &[u8] = include_bytes!("../admin/index.html");
const APP_JS: &[u8] = include_bytes!("../admin/app.js");
const APP_CSS: &[u8] = include_bytes!("../admin/app.css");

#[derive(Clone, Debug)]
pub struct AdminAssets {
    index_html: Blob,
    app_js: Blob,
    app_css: Blob,
}

impl AdminAssets {
    pub fn load() -> std::io::Result<Self> {
        Ok(Self {
            index_html: Blob::new(INDEX_HTML.to_vec(), "text/html;charset=UTF-8")?,
            app_js: Blob::new(APP_JS.to_vec(), "text/javascript;charset=UTF-8")?,
            app_css: Blob::new(APP_CSS.to_vec(), "text/css;charset=UTF-8")?,
        })
    }

    pub fn respond(&self, path: &str, headers: &HeaderMap, method: &Method) -> Option<Response> {
        let blob = match path {
            "/" | "/index.html" => &self.index_html,
            "/app.js" => &self.app_js,
            "/app.css" => &self.app_css,
            _ => return None,
        };
        Some(blob.respond(headers, method, "private, no-cache"))
    }
}
