**Linux2 优化报告**

**概述**
本次优化针对高频 `terminal.output` 场景下的 CPU 与磁盘 IO 开销，完成了 1、2、3 三项改造：日志降噪、历史 trim 延迟、历史写盘异步批量化。

**变更清单**
1. 日志降噪：跳过 `terminal.output` 的逐条日志打印，减少高频字符串拼接与 I/O。文件：`/root/phoneshell/linux2/src/relay/relay-server.ts`
2. 历史 trim 延迟：将每次 append 触发的 trim 改为定时触发，避免频繁扫描与重写大文件，默认间隔 30s。文件：`/root/phoneshell/linux2/src/store/terminal-history-store.ts`
3. 历史写盘异步批量化：append 改为内存缓冲，按 50ms 批量写盘，单会话缓冲超过 256KB 时立即 flush，并在 `getPage` / `readAll` 前同步 flush 该会话以确保读取完整。文件：`/root/phoneshell/linux2/src/store/terminal-history-store.ts`

**行为影响说明**
- 持久化能力不变，仍保留最近 500 万字符。
- 历史读取不变，读取前同步 flush 确保客户端获取最新数据。
- 进程异常退出时可能丢失最近约 50ms 的历史数据，仅影响历史持久化，不影响实时转发。

**部署与生效**
以下仅在 `linux2` 源码生效，需重新构建并部署到运行目录。

```bash
cd /root/phoneshell/linux2
npm run build
# 将 dist 产物部署到运行目录（示例）
# rsync -a --delete /root/phoneshell/linux2/dist/ /opt/phoneshell/dist/
# systemctl restart phoneshell
```

**后续可选优化（未做）**
- 输出合并与背压，可进一步降低高频 TUI 场景 CPU，但会改变消息粒度或引入丢弃策略。
