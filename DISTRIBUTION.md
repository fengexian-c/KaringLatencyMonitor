# Karing 延迟监控 - 使用说明

1. 先启动 Karing，并在 Karing 中启用 Clash API / External Controller。
2. 运行 `KaringLatencyMonitor.App.exe`。
3. 展开“连接与采集设置”，填写控制器地址和 API secret；默认地址为 `http://127.0.0.1:3057`。
4. 点击“刷新分组”，选择节点组，再勾选需要采集与统计的节点。
5. 点击“立即采集”，或开启自动采集。

默认的 `KaringLatencyMonitor-win-x64` 是 NativeAOT 自包含版本，无需另行安装 .NET 或 Windows App Runtime。文件名带 `-lean` 的精简版本不需要 .NET，但要求系统已经安装 Windows App Runtime 2.0。

无论便携运行、精简包还是普通构建，应用数据都固定保存在 EXE 同级目录：

```text
data\
```

- `latency.db`：SQLite 时间序列、采集批次、节点组与选择状态。
- `settings.json`：非敏感配置。
- API secret：Windows Credential Locker。
- `startup.log`：启动诊断日志。

请把整个应用目录放在当前用户可写的位置（例如文档或其他数据盘目录），不要直接放入需要管理员权限写入的 `Program Files`。

关闭窗口时应用会缩入系统托盘；请从托盘菜单选择“退出”以完全结束。

托盘状态会主动回收窗口临时缓冲并裁减工作集；再次打开时系统会按需恢复页面内存，这是正常行为。
