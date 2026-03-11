# PhoneShell 部署指南

## 传输安全 (TLS/WSS)

PhoneShell 的 WebSocket 服务默认使用 `ws://`（明文）。在生产环境中，建议通过反向代理提供 `wss://`（TLS 加密）。

### Nginx 反向代理配置

```nginx
server {
    listen 443 ssl;
    server_name phoneshell.example.com;

    ssl_certificate     /etc/letsencrypt/live/phoneshell.example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/phoneshell.example.com/privkey.pem;

    location /ws/ {
        proxy_pass http://127.0.0.1:9000/ws/;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_read_timeout 86400;
        proxy_send_timeout 86400;
    }

    # Health check and status endpoints
    location /healthz {
        proxy_pass http://127.0.0.1:9000/healthz;
    }

    location /status {
        proxy_pass http://127.0.0.1:9000/status;
    }

    # Web panel (if enabled)
    location /panel {
        proxy_pass http://127.0.0.1:9000/panel;
    }
}
```

### Caddy 反向代理配置

```caddyfile
phoneshell.example.com {
    reverse_proxy /ws/* localhost:9000
    reverse_proxy /healthz localhost:9000
    reverse_proxy /status localhost:9000
    reverse_proxy /panel localhost:9000
}
```

Caddy 会自动从 Let's Encrypt 获取证书。

### 客户端连接

使用反向代理后，客户端连接地址应改为：

```
wss://phoneshell.example.com/ws/
```

### 注意事项

- WebSocket 连接需要 `proxy_read_timeout` 设置较大值（至少 3600），否则长时间空闲的连接会被断开
- PhoneShell 内置了 keep-alive（20 秒间隔），但仍建议设置较大的超时
- 群组密钥（GroupSecret）通过 HTTP header 发送，TLS 加密保护其安全性

## Linux Headless 部署

### systemd 服务

创建 `/etc/systemd/system/phoneshell.service`：

```ini
[Unit]
Description=PhoneShell Headless Server
After=network.target

[Service]
Type=simple
User=phoneshell
WorkingDirectory=/opt/phoneshell
ExecStart=/usr/bin/dotnet /opt/phoneshell/PhoneShell.Headless.dll --mode server --port 9000
Restart=always
RestartSec=5

[Install]
WantedBy=multi-user.target
```

启动：

```bash
sudo systemctl enable phoneshell
sudo systemctl start phoneshell
```

### Docker 部署

```dockerfile
FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY publish/ .
EXPOSE 9000
ENTRYPOINT ["dotnet", "PhoneShell.Headless.dll", "--mode", "server", "--port", "9000"]
```

```bash
docker build -t phoneshell .
docker run -d -p 9000:9000 --name phoneshell phoneshell
```
