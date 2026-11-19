use std::sync::OnceLock;

use axum::http::StatusCode;
use regex::Regex;
use serde::{Deserialize, Deserializer, Serialize};
use url::Url;

use crate::error::ApiError;

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Pack {
    #[serde(default, deserialize_with = "null_as_empty")]
    pub id: String,
    #[serde(default, deserialize_with = "null_as_empty")]
    pub name: String,
    #[serde(default, deserialize_with = "null_as_empty")]
    pub description: String,
    #[serde(default, deserialize_with = "null_as_empty")]
    pub version: String,
    #[serde(default, deserialize_with = "null_as_empty")]
    pub minecraft: String,
    #[serde(default, deserialize_with = "null_as_empty")]
    pub icon: String,
    #[serde(default, deserialize_with = "null_as_empty")]
    pub cover_url: String,
    #[serde(default, deserialize_with = "null_as_empty")]
    pub background: String,
    #[serde(default, deserialize_with = "null_as_empty")]
    pub download_url: String,
    #[serde(default, deserialize_with = "null_as_empty_vec")]
    pub backup_urls: Vec<String>,
    #[serde(default, deserialize_with = "null_as_empty")]
    pub sha256: String,
    #[serde(default, deserialize_with = "null_as_empty")]
    pub file_name: String,
    #[serde(default, deserialize_with = "null_as_empty")]
    pub share_url: String,
    #[serde(default, deserialize_with = "null_as_empty")]
    pub share_path: String,
    #[serde(default, deserialize_with = "null_as_false")]
    pub enabled: bool,
    #[serde(default, deserialize_with = "null_as_zero")]
    pub sort_order: i32,
}

fn null_as_empty<'de, D>(deserializer: D) -> Result<String, D::Error>
where
    D: Deserializer<'de>,
{
    Ok(Option::<String>::deserialize(deserializer)?.unwrap_or_default())
}

fn null_as_empty_vec<'de, D>(deserializer: D) -> Result<Vec<String>, D::Error>
where
    D: Deserializer<'de>,
{
    Ok(Option::<Vec<String>>::deserialize(deserializer)?.unwrap_or_default())
}

fn null_as_false<'de, D>(deserializer: D) -> Result<bool, D::Error>
where
    D: Deserializer<'de>,
{
    Ok(Option::<bool>::deserialize(deserializer)?.unwrap_or(false))
}

fn null_as_zero<'de, D>(deserializer: D) -> Result<i32, D::Error>
where
    D: Deserializer<'de>,
{
    Ok(Option::<i32>::deserialize(deserializer)?.unwrap_or(0))
}

fn id_regex() -> &'static Regex {
    static RE: OnceLock<Regex> = OnceLock::new();
    RE.get_or_init(|| Regex::new(r"^[a-z0-9][a-z0-9_-]{0,63}$").expect("valid regex"))
}

fn sha256_regex() -> &'static Regex {
    static RE: OnceLock<Regex> = OnceLock::new();
    RE.get_or_init(|| Regex::new(r"^(?:[a-fA-F0-9]{64})?$").expect("valid regex"))
}

fn filename_regex() -> &'static Regex {
    static RE: OnceLock<Regex> = OnceLock::new();
    RE.get_or_init(|| {
        Regex::new(r#"^[^\\/:*?"<>|\x00-\x1F\x7F]+\.(?i:zip|mrpack)$"#).expect("valid regex")
    })
}

#[inline]
pub fn is_java_blank(s: &str) -> bool {
    s.trim_matches(|c: char| c <= '\u{0020}').is_empty()
}

#[inline]
fn utf16_len(s: &str) -> usize {
    s.encode_utf16().count()
}

impl Pack {
    pub fn validate(&self) -> Result<(), ApiError> {
        let mut violations: Vec<(&'static str, &'static str)> = Vec::new();

        if self.backup_urls.len() > 8 {
            violations.push(("backupUrls", "size must be between 0 and 8"));
        }
        for url in &self.backup_urls {
            if is_java_blank(url) {
                violations.push(("backupUrls", "must not be blank"));
            }
            if utf16_len(url) > 2048 {
                violations.push(("backupUrls", "size must be between 0 and 2048"));
            }
        }
        if utf16_len(&self.background) > 2048 {
            violations.push(("background", "size must be between 0 and 2048"));
        }
        if utf16_len(&self.cover_url) > 2048 {
            violations.push(("coverUrl", "size must be between 0 and 2048"));
        }
        if utf16_len(&self.description) > 4000 {
            violations.push(("description", "size must be between 0 and 4000"));
        }
        if is_java_blank(&self.download_url) {
            violations.push(("downloadUrl", "must not be blank"));
        }
        if utf16_len(&self.download_url) > 2048 {
            violations.push(("downloadUrl", "size must be between 0 and 2048"));
        }
        if is_java_blank(&self.file_name) {
            violations.push(("fileName", "must not be blank"));
        }
        if utf16_len(&self.file_name) > 160 {
            violations.push(("fileName", "size must be between 0 and 160"));
        }
        if utf16_len(&self.icon) > 2048 {
            violations.push(("icon", "size must be between 0 and 2048"));
        }
        if is_java_blank(&self.id) {
            violations.push(("id", "must not be blank"));
        }
        if !id_regex().is_match(&self.id) {
            violations.push(("id", "需要匹配正则表达式\"[a-z0-9][a-z0-9_-]{0,63}\""));
        }
        if utf16_len(&self.minecraft) > 40 {
            violations.push(("minecraft", "size must be between 0 and 40"));
        }
        if is_java_blank(&self.name) {
            violations.push(("name", "must not be blank"));
        }
        if utf16_len(&self.name) > 120 {
            violations.push(("name", "size must be between 0 and 120"));
        }
        if !sha256_regex().is_match(&self.sha256) {
            violations.push(("sha256", "must match \"(?:[a-fA-F0-9]{64})?\""));
        }
        if utf16_len(&self.share_path) > 512 {
            violations.push(("sharePath", "size must be between 0 and 512"));
        }
        if utf16_len(&self.share_url) > 2048 {
            violations.push(("shareUrl", "size must be between 0 and 2048"));
        }
        if !(-100_000..=100_000).contains(&self.sort_order) {
            violations.push(("sortOrder", "must be between -100000 and 100000"));
        }
        if is_java_blank(&self.version) {
            violations.push(("version", "must not be blank"));
        }
        if utf16_len(&self.version) > 80 {
            violations.push(("version", "size must be between 0 and 80"));
        }

        if let Some((path, msg)) = violations.into_iter().min_by_key(|(p, _)| *p) {
            return Err(ApiError::new(
                StatusCode::BAD_REQUEST,
                format!("{path}: {msg}"),
            ));
        }

        self.validate_links()
    }

    pub fn validate_links(&self) -> Result<(), ApiError> {
        for link in [
            &self.icon,
            &self.cover_url,
            &self.background,
            &self.download_url,
            &self.share_url,
        ] {
            if !link.is_empty() {
                validate_url(link)?;
            }
        }
        for link in &self.backup_urls {
            validate_url(link)?;
        }
        if !filename_regex().is_match(&self.file_name)
            || self.file_name.ends_with(' ')
            || self.file_name.starts_with('.')
        {
            return Err(ApiError::new(
                StatusCode::BAD_REQUEST,
                "文件名必须是合法的 .zip 或 .mrpack 文件名，不得含路径",
            ));
        }
        Ok(())
    }

    pub fn public_view(&self) -> Self {
        let mut view = self.clone();
        if is_java_blank(&view.icon) {
            view.icon = view.cover_url.clone();
        }
        if is_java_blank(&view.background) {
            view.background = view.cover_url.clone();
        }
        view
    }
}

fn validate_url(link: &str) -> Result<(), ApiError> {
    let invalid = || {
        ApiError::new(
            StatusCode::BAD_REQUEST,
            "URL 必须为不含凭据或片段的完整 HTTP(S) 地址",
        )
    };
    if link.chars().any(|c| c.is_whitespace() || c.is_control()) || link.contains('#') {
        return Err(invalid());
    }
    let Some((_, after_scheme)) = link.split_once("://") else {
        return Err(invalid());
    };
    let authority = after_scheme
        .split(['/', '?', '#'])
        .next()
        .unwrap_or_default();
    if authority.contains('@') {
        return Err(invalid());
    }
    let parsed = Url::parse(link).map_err(|_| invalid())?;
    let scheme = parsed.scheme();
    if !(scheme.eq_ignore_ascii_case("http") || scheme.eq_ignore_ascii_case("https")) {
        return Err(invalid());
    }
    let Some(host) = parsed.host_str() else {
        return Err(invalid());
    };
    if host.is_empty() || !parsed.username().is_empty() || parsed.password().is_some() {
        return Err(invalid());
    }
    if parsed.fragment().is_some() || parsed.port() == Some(0) {
        return Err(invalid());
    }
    Ok(())
}
