由于 C 盘空间不够用了就写了一个小东西来把 XIVLauncher 迁移出去

本质是用 WPF 包装了一下 mklink 指令

使用须知：

- 需要管理员权限以创建 mklink 链接
- 使用前请确保任何 FF14 与 XIVLauncher 相关进程都已关闭