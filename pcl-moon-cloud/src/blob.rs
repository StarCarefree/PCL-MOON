use std::io::Write;

use axum::{
    body::Body,
    http::{HeaderMap, HeaderValue, Method, StatusCode, header},
    response::Response,
};
use bytes::Bytes;
use flate2::{Compression, GzBuilder};
use sha2::{Digest, Sha256};

#[derive(Clone, Debug)]
pub struct Blob {
    plain: Bytes,
    gzip: Bytes,
    plain_tag: HeaderValue,
    gzip_tag: HeaderValue,
    content_type: HeaderValue,
}

impl Blob {
    pub fn new(plain: Vec<u8>, content_type: &'static str) -> std::io::Result<Self> {
        // Match Java GZIPOutputStream header (mtime=0, os=255).
        let mut encoder = GzBuilder::new()
            .operating_system(255)
            .write(Vec::new(), Compression::default());
        encoder.write_all(&plain)?;
        let gzip = encoder.finish()?;

        let plain_tag = make_tag(&plain);
        let gzip_tag = make_tag(&gzip);
        let content_type = HeaderValue::from_static(content_type);

        Ok(Self {
            plain: Bytes::from(plain),
            gzip: Bytes::from(gzip),
            plain_tag,
            gzip_tag,
            content_type,
        })
    }

    pub fn plain_bytes(&self) -> &[u8] {
        &self.plain
    }

    pub fn gzip_bytes(&self) -> &[u8] {
        &self.gzip
    }

    pub fn plain_etag(&self) -> &str {
        // INFALLIBLE: SHA-256 hex quoted tag is pure ASCII.
        self.plain_tag.to_str().unwrap_or("")
    }

    pub fn gzip_etag(&self) -> &str {
        // INFALLIBLE: SHA-256 hex quoted tag is pure ASCII.
        self.gzip_tag.to_str().unwrap_or("")
    }

    pub fn respond(
        &self,
        req_headers: &HeaderMap,
        method: &Method,
        cache_policy: &'static str,
    ) -> Response {
        let accept_encoding = req_headers
            .get(header::ACCEPT_ENCODING)
            .and_then(|v| v.to_str().ok());
        let compressed = self.gzip.len() < self.plain.len() && accepts_gzip(accept_encoding);
        let bytes = if compressed { &self.gzip } else { &self.plain };
        let etag = if compressed {
            &self.gzip_tag
        } else {
            &self.plain_tag
        };

        let mut builder = Response::builder()
            .header(header::CONTENT_TYPE, self.content_type.clone())
            .header(
                header::CACHE_CONTROL,
                HeaderValue::from_static(cache_policy),
            )
            .header(header::ETAG, etag.clone())
            .header(header::VARY, HeaderValue::from_static("Accept-Encoding"));

        if compressed {
            builder = builder.header(header::CONTENT_ENCODING, HeaderValue::from_static("gzip"));
        }

        let if_none_match = req_headers
            .get(header::IF_NONE_MATCH)
            .and_then(|v| v.to_str().ok());
        let etag_str = etag.to_str().unwrap_or("");
        if matches_etag(if_none_match, etag_str) {
            return builder
                .status(StatusCode::NOT_MODIFIED)
                .body(Body::empty())
                .unwrap_or_else(|_| Response::new(Body::empty()));
        }

        builder = builder
            .status(StatusCode::OK)
            .header(header::CONTENT_LENGTH, bytes.len().to_string());
        let body = if *method == Method::HEAD {
            Body::empty()
        } else {
            Body::from(bytes.clone())
        };
        builder
            .body(body)
            .unwrap_or_else(|_| Response::new(Body::empty()))
    }
}

fn make_tag(bytes: &[u8]) -> HeaderValue {
    let digest = Sha256::digest(bytes);
    let mut s = String::with_capacity(66);
    s.push('"');
    for b in digest {
        use std::fmt::Write;
        let _ = write!(&mut s, "{b:02x}");
    }
    s.push('"');
    // INFALLIBLE: quoted lowercase hex contains only ASCII graphic bytes.
    HeaderValue::from_str(&s).expect("valid ASCII ETag")
}

pub fn matches_etag(value: Option<&str>, tag: &str) -> bool {
    let Some(value) = value else {
        return false;
    };
    let weak = format!("W/{tag}");
    for candidate in value.split(',') {
        let candidate = candidate.trim();
        if candidate == "*" || candidate == tag || candidate == weak {
            return true;
        }
    }
    false
}

pub fn accepts_gzip(value: Option<&str>) -> bool {
    let Some(value) = value else {
        return false;
    };
    let mut gzip_quality: Option<f64> = None;
    let mut wildcard_quality: Option<f64> = None;

    for entry in value.to_ascii_lowercase().split(',') {
        let mut parts = entry.trim().split(';');
        let Some(coding) = parts.next() else {
            continue;
        };
        let coding = coding.trim();
        let mut quality = 1.0_f64;
        for part in parts {
            let parameter = part.trim();
            if let Some(raw_q) = parameter.strip_prefix("q=") {
                quality = raw_q.trim().parse::<f64>().unwrap_or(0.0);
            }
        }
        if !quality.is_finite() || !(0.0..=1.0).contains(&quality) {
            quality = 0.0;
        }
        if coding == "gzip" {
            gzip_quality = Some(quality);
        }
        if coding == "*" {
            wildcard_quality = Some(quality);
        }
    }

    gzip_quality.or(wildcard_quality).unwrap_or(0.0) > 0.0
}
