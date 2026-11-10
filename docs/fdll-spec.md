# Fold 引擎 .fdll 模块规范
`.fdll` 模块本质是一个数据包，包含：
1. `manifest.json`：元数据（id, name, tags）。
2. `timeline.json`：时间轴（包含 time(秒), action(动作), url(CDN地址)）。
3. 在服务端渲染时，因为SLAC限制，不推高分辨率图片，而是采用“像素监控风格”或“纯文本广播”。
