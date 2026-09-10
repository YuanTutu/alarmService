# PictureReceiver / 报警服务器模拟器 - 摄像头抓拍数据接收器

> 当前版本 **v2.2** · 作者 yuanbo6 · 开发时间 2026-09-10
> 版本历史：v1.0 控制台版 → v2.0 WinForms 界面版（内嵌 https 证书）→ v2.1 标题与窗口布局调整 → v2.2 按钮高 DPI 等高对齐

## 一、开发目的

在人脸抓拍类安防项目的开发与现场联调中，需要一个"扮演报警服务器/接收平台"的本机程序：

- **接收**：实时接收网络摄像头（人脸抓拍机）通过 HTTP/HTTPS POST 推送的抓拍数据（multipart/form-data）
- **还原**：把推送内容自动拆解成可直接查看的 JSON 元数据（设备 IP/MAC、时间、人脸坐标、置信度等）、人脸小图、全景大图，供联调人员核对设备推送的字段与图片是否正确
- **回放**：配合 curl 重放真实抓包样本（`recv_*.bin`），在没有相机/平台的开发阶段做开发与回归测试

现场环境约束（客户机器可能离线、无管理员权限、不允许安装任何运行时）决定了它必须是一个**免安装、双击即用的单 exe**。

## 二、开发设计思路

1. **单文件交付**：基于 Windows 自带的 .NET Framework 4，用系统自带 csc.exe 编译，零第三方依赖；exe 约 36KB，拷贝即用，关闭窗口即停。
2. **手写极简 HTTP 服务**：不用 HttpListener（依赖系统 URL ACL 且 https 配置繁琐），直接 `TcpListener` + 手写请求头解析 + 线程池处理连接；只开放 `/test` 一个路径，行为完全可控、可预测。
3. **内存拆包，不落中间文件**：收到 multipart 报文后在内存中按 boundary 切分（自写字节级解析器），直接落盘 `meta.json + face.jpg + bg.jpg`；图片格式按文件头魔数识别（jpg/png/bmp），不信任 Content-Type 声明；拆包失败保留 `_unparsed.bin` 兜底，避免数据丢失。
4. **HTTPS 零证书配置**：抓拍设备不校验服务端证书，因此证书的唯一作用是提供 TLS 握手材料——在**编译期**生成一张 100 年期自签证书并内嵌进 exe。由此部署者和设备端都不需要配置任何证书，交付链路里也不再有 `server.pfx`、证书密码、`gen-cert.ps1` 这些易错项。
5. **界面即配置器**：WinForms 界面只做三件事——改配置、看状态、看日志。配置仍持久化到同目录 `config.txt`（纯文本 key=value，可手工编辑），界面只是它的可视化编辑器，两种方式互通；监听 IP 下拉自动枚举本机网卡，规避"配置里的 IP 本机不存在"这一现场最常见故障。
6. **对高 DPI 稳健**：固定尺寸窗口 + 运行时按文本框实际高度对齐按钮，不依赖会随缩放出错的嵌套锚定。
7. **可测试性**：保留 3 个真实设备抓包样本（`recv_*.bin`），`curl --data-binary` 即可整链路回归（http 拆包、404、https 握手）。

## 三、目录结构

```
alarmService/                    (git 仓库)
├── PictureReceiver.cs           主程序源码（单文件：界面 + HTTP/TLS 服务 + multipart 解析）
├── MultipartExtract.cs          独立离线拆包工具源码（开发期诊断用，不随交付）
├── README.md                    本文档
├── .gitignore                   忽略构建产物 / 运行时配置 / 抓包样本
├── recv_*.bin                   真实设备抓包样本（仅本地保留：含真实人脸图像与设备信息，不入仓库）
├── deliver/                     交付目录（git 忽略）：PictureReceiver.exe + README.md，config.txt 由程序自动生成
└── PictureReceiver-v2.2-win.zip GitHub release 资产（git 忽略）：exe + 使用说明
```

## 四、源码与编译

Windows 自带编译器，无需安装 VS（Git Bash 写法）：

```bash
C:/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe -nologo -target:winexe -optimize+ \
  -r:System.dll -r:System.Core.dll -r:System.Drawing.dll -r:System.Windows.Forms.dll \
  -out:PictureReceiver.exe PictureReceiver.cs
```

编译产物测试通过后复制到 `deliver/`，并重新打包：

```bash
powershell Compress-Archive -Path deliver/PictureReceiver.exe,deliver/README.md -DestinationPath deliver.zip -Force
```

版本号定义见源码头注释（按重构/修复轮次递增）；作者、版本、开发时间同时写入 exe 文件属性（AssemblyInfo）、窗口标题和启动日志。

## 五、使用方法（交付版）

1. 双击 `deliver/PictureReceiver.exe`，自动按上次保存的配置开始监听
2. 界面中选协议（http/https）、监听 IP、端口、保存目录，点 **应用并重启监听**
3. 设备端推送地址：`http://本机IP:端口/test`（https 同理，无需配置证书）
4. 首次运行如弹出防火墙提示，勾选"专用网络/公用网络"并允许访问

## 六、接收与保存规则

- 只处理 `/test` 路径，其他返回 404；接口约定 POST，数据在请求体
- multipart 抓拍推送在内存直接拆包：`faceCapture → *_meta.json`、`faceImage → *_face.jpg`、`backgroundImage → *_bg.jpg`
- 非 multipart 请求体按时间戳原样保存，扩展名按 Content-Type 和文件头推断

## 七、测试方法

```bash
# multipart 抓包数据（自动拆为 json + 两张图）
curl -X POST -H "Content-Type: multipart/form-data" --data-binary @recv_20260909_153326_547_1.bin http://127.0.0.1:5578/test

# 普通原始数据
curl -X POST --data-binary "hello" http://127.0.0.1:5578/test

# HTTPS（内置自签证书，加 -k）
curl -k -X POST -H "Content-Type: multipart/form-data" --data-binary @recv_20260909_153326_547_1.bin https://127.0.0.1:5578/test

# 验证 404（应返回 use /test）
curl http://127.0.0.1:5578/other
```

返回 `OK` 即接收成功，到保存目录查看文件；窗口下方运行日志有每条请求与拆包明细。

## 八、常见问题

- **设备连不上**：防火墙放行；监听 IP 用 0.0.0.0；端口未占用；窗口开着（关掉即停止）
- **改配置不生效**：点"应用并重启监听"
- **日志在哪**：窗口下方"运行日志"区实时显示，替代了原控制台输出
- **收到数据但图片打不开**：数据非标准 multipart（会留 `_unparsed.bin`），用 `MultipartExtract` 工具（源码在本目录）离线拆解并保留样本
