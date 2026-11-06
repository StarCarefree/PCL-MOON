package io.pclmoon.cloud;

import com.fasterxml.jackson.databind.ObjectMapper;
import jakarta.servlet.*;
import jakarta.servlet.http.*;
import java.io.*;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.util.Map;
import java.util.Set;
import org.springframework.core.Ordered;
import org.springframework.core.annotation.Order;
import org.springframework.stereotype.Component;
import org.springframework.web.filter.OncePerRequestFilter;

/** Route isolation uses the accepted socket port, never Host or forwarded headers. */
@Component
@Order(Ordered.HIGHEST_PRECEDENCE)
final class PortGuard extends OncePerRequestFilter {
    private static final int MAX_BODY = 128 * 1024;
    private static final Set<String> ASSETS = Set.of("/", "/index.html", "/app.js", "/app.css");
    private final Ports ports;
    private final byte[] authorization;
    private final ObjectMapper mapper;

    PortGuard(Ports ports, CloudProperties properties, ObjectMapper mapper) {
        this.ports = ports;
        this.mapper = mapper;
        authorization = ("Bearer " + properties.adminToken()).getBytes(StandardCharsets.UTF_8);
    }

    @Override
    protected void doFilterInternal(HttpServletRequest request, HttpServletResponse response, FilterChain chain)
            throws ServletException, IOException {
        response.setHeader("X-Content-Type-Options", "nosniff");
        response.setHeader("X-Frame-Options", "DENY");
        response.setHeader("Referrer-Policy", "no-referrer");
        response.setHeader("Content-Security-Policy", "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' https: http:; connect-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'");
        String path = request.getRequestURI();
        String method = request.getMethod();
        boolean read = method.equals("GET") || method.equals("HEAD");
        if (!ports.isAdmin(request.getLocalPort())) {
            boolean publicPath = path.equals("/index.json") || path.equals("/catalog.json") || path.equals("/healthz") || path.equals("/api/v1/packs")
                    || path.matches("/api/v1/packs/[a-z0-9][a-z0-9_-]{0,63}(/download)?");
            if (!publicPath) { error(response, 404, "资源不存在"); return; }
            if (!read) { response.setHeader("Allow", "GET, HEAD"); error(response, 405, "获取端口仅支持 GET/HEAD"); return; }
        } else {
            if (read && (ASSETS.contains(path) || path.equals("/healthz"))) { chain.doFilter(request, response); return; }
            if (!path.startsWith("/api/admin/")) { error(response, 404, "资源不存在"); return; }
            response.setHeader("Cache-Control", "no-store");
            String supplied = request.getHeader("Authorization");
            if (supplied == null || !MessageDigest.isEqual(authorization, supplied.getBytes(StandardCharsets.UTF_8))) {
                response.setHeader("WWW-Authenticate", "Bearer");
                error(response, 401, "管理令牌错误或缺失"); return;
            }
            if (!read && !Set.of("POST", "PUT", "DELETE").contains(method)) {
                error(response, 405, "请求方法不支持"); return;
            }
            if (!read) {
                if (request.getContentLengthLong() > MAX_BODY) { error(response, 413, "请求体超过 128 KiB"); return; }
                byte[] body = request.getInputStream().readNBytes(MAX_BODY + 1);
                if (body.length > MAX_BODY) { error(response, 413, "请求体超过 128 KiB"); return; }
                request = new BufferedRequest(request, body);
            }
        }
        chain.doFilter(request, response);
    }

    private void error(HttpServletResponse response, int status, String message) throws IOException {
        response.setStatus(status);
        response.setHeader("Cache-Control", "no-store");
        response.setContentType("application/json;charset=UTF-8");
        mapper.writeValue(response.getOutputStream(), Map.of("error", message));
    }

    private static final class BufferedRequest extends HttpServletRequestWrapper {
        private final byte[] body;
        BufferedRequest(HttpServletRequest request, byte[] body) { super(request); this.body = body; }
        @Override public ServletInputStream getInputStream() {
            ByteArrayInputStream stream = new ByteArrayInputStream(body);
            return new ServletInputStream() {
                @Override public int read() { return stream.read(); }
                @Override public int read(byte[] target, int offset, int length) { return stream.read(target, offset, length); }
                @Override public boolean isFinished() { return stream.available() == 0; }
                @Override public boolean isReady() { return true; }
                @Override public void setReadListener(ReadListener listener) { throw new UnsupportedOperationException(); }
            };
        }
        @Override public BufferedReader getReader() { return new BufferedReader(new InputStreamReader(getInputStream(), StandardCharsets.UTF_8)); }
    }
}
