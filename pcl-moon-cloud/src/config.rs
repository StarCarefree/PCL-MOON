use std::env;
use std::path::PathBuf;
use std::sync::OnceLock;

use regex::Regex;

#[derive(Debug, Clone)]
pub struct Config {
    pub public_port: u16,
    pub public_bind: String,
    pub admin_port: u16,
    pub admin_bind: String,
    pub admin_token: String,
    pub data_dir: PathBuf,
}

#[derive(Default)]
struct CliOverrides {
    public_port: Option<String>,
    public_bind: Option<String>,
    admin_port: Option<String>,
    admin_bind: Option<String>,
    admin_token: Option<String>,
    data_dir: Option<String>,
}

fn token_regex() -> &'static Regex {
    static RE: OnceLock<Regex> = OnceLock::new();
    RE.get_or_init(|| Regex::new(r"^[A-Za-z0-9_-]+$").expect("valid regex"))
}

fn parse_cli_args<I, S>(args: I) -> CliOverrides
where
    I: IntoIterator<Item = S>,
    S: AsRef<str>,
{
    let mut out = CliOverrides::default();
    let mut iter = args.into_iter();
    while let Some(arg) = iter.next() {
        let s = arg.as_ref();
        if let Some(val) = s.strip_prefix("--server.port=") {
            out.public_port = Some(val.to_string());
        } else if let Some(val) = s.strip_prefix("--port=") {
            out.public_port = Some(val.to_string());
        } else if s == "--port" {
            if let Some(next) = iter.next() {
                out.public_port = Some(next.as_ref().to_string());
            }
        } else if let Some(val) = s.strip_prefix("--pclmoon.public-bind=") {
            out.public_bind = Some(val.to_string());
        } else if let Some(val) = s.strip_prefix("--pclmoon.admin-port=") {
            out.admin_port = Some(val.to_string());
        } else if let Some(val) = s.strip_prefix("--pclmoon.admin-bind=") {
            out.admin_bind = Some(val.to_string());
        } else if let Some(val) = s.strip_prefix("--pclmoon.admin-token=") {
            out.admin_token = Some(val.to_string());
        } else if let Some(val) = s.strip_prefix("--pclmoon.data-dir=") {
            out.data_dir = Some(val.to_string());
        }
    }
    out
}

fn env_or(primary: &str, legacy: &str) -> Option<String> {
    env::var(primary).ok().or_else(|| env::var(legacy).ok())
}

fn parse_port(raw: &str, err_msg: &str) -> Result<u16, String> {
    let parsed: i32 = raw.trim().parse().map_err(|_| err_msg.to_string())?;
    u16::try_from(parsed).map_err(|_| err_msg.to_string())
}

impl Config {
    pub fn from_env_and_args() -> Result<Self, String> {
        Self::from_sources(env::args().skip(1), env_or)
    }

    pub fn from_sources<I, S, F>(args: I, get_env: F) -> Result<Self, String>
    where
        I: IntoIterator<Item = S>,
        S: AsRef<str>,
        F: Fn(&str, &str) -> Option<String>,
    {
        let cli = parse_cli_args(args);

        let public_port_raw = cli
            .public_port
            .or_else(|| get_env("PCL_MOON_PUBLIC_PORT", "MOONCL_PUBLIC_PORT"))
            .or_else(|| env::var("PORT").ok())
            .unwrap_or_else(|| "6664".to_string());
        let public_port = parse_port(&public_port_raw, "公开端口无效")?;

        let public_bind = cli
            .public_bind
            .or_else(|| get_env("PCL_MOON_PUBLIC_BIND", "MOONCL_PUBLIC_BIND"))
            .unwrap_or_else(|| "0.0.0.0".to_string());

        let admin_port_raw = cli
            .admin_port
            .or_else(|| get_env("PCL_MOON_ADMIN_PORT", "MOONCL_ADMIN_PORT"))
            .unwrap_or_else(|| "8081".to_string());
        let admin_port = parse_port(&admin_port_raw, "管理端口无效")?;

        let admin_bind = cli
            .admin_bind
            .or_else(|| get_env("PCL_MOON_ADMIN_BIND", "MOONCL_ADMIN_BIND"))
            .unwrap_or_else(|| "127.0.0.1".to_string());

        let admin_token = cli
            .admin_token
            .or_else(|| get_env("PCL_MOON_ADMIN_TOKEN", "MOONCL_ADMIN_TOKEN"))
            .unwrap_or_default();

        let data_dir = PathBuf::from(
            cli.data_dir
                .or_else(|| get_env("PCL_MOON_DATA_DIR", "MOONCL_DATA_DIR"))
                .unwrap_or_else(|| "./data".to_string()),
        );

        let cfg = Self {
            public_port,
            public_bind,
            admin_port,
            admin_bind,
            admin_token,
            data_dir,
        };
        cfg.validate()?;
        Ok(cfg)
    }

    pub fn validate(&self) -> Result<(), String> {
        let len = self.admin_token.encode_utf16().count();
        if !(32..=256).contains(&len) || !token_regex().is_match(&self.admin_token) {
            return Err(
                "PCL_MOON_ADMIN_TOKEN 必须是 32–256 位随机字母、数字、下划线或连字符".to_string(),
            );
        }
        if self.public_port > 0 && self.public_port == self.admin_port {
            return Err("获取端口和管理端口不能相同".to_string());
        }
        Ok(())
    }
}
