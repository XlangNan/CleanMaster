# CleanMaster 项目骨架

一个 Windows 垃圾清理工具骨架:单文件可执行、扫描/展示/确认/清理分离、
先移入隔离区再延迟删除,内置对聊天软件用户数据、浏览器登录信息的硬编码保护。

## 环境要求(在你自己的 Windows 电脑上)

- Windows 10/11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Visual Studio 2022(可选,也可以纯命令行)

## 本地运行(开发调试)

```powershell
cd CleanMaster
dotnet run
```

> 注意:调试运行时如果没有以管理员身份启动 Visual Studio / 终端,
> 涉及 `C:\Windows\Temp`、Windows Update 缓存等系统目录的扫描会被跳过
> 并在界面上标注"权限不足",这是预期行为,不是 bug。

## 电脑上没有 .NET / Visual Studio 环境?用 GitHub Actions 云端编译(推荐)

不需要本地装任何东西,项目里已经带了
`.github/workflows/build.yml`,可以让 GitHub 的云端 Windows 主机帮你编译。

**操作步骤:**

1. 在 GitHub 上新建一个仓库(比如叫 `CleanMaster`),设为 Private 都没问题
2. 把这个项目文件夹整个推上去:

   ```powershell
   cd CleanMaster
   git init
   git add .
   git commit -m "init"
   git branch -M main
   git remote add origin https://github.com/你的用户名/CleanMaster.git
   git push -u origin main
   ```

3. 推送后打开 GitHub 仓库页面的 **Actions** 标签页,会自动触发一次编译
   (如果没自动触发,点击左侧 "Build CleanMaster" → 右侧 "Run workflow" 手动触发一次)
4. 等编译完成(通常 1-3 分钟),进入这次运行的详情页,下方 **Artifacts** 区域
   会有一个 `CleanMaster-win-x64` 的下载包,里面就是编译好的 `CleanMaster.exe`
5. 下载解压后,`CleanMaster.exe` 可以直接拷到任何 Windows 10/11 电脑双击运行

这个流程完全在云端完成,你本地只需要装一个 `git` 命令行工具(或者直接在
GitHub 网页上用 "Upload files" 功能上传整个文件夹,连 git 都不用装)。

> 如果连 git 都不想装:GitHub 仓库页面点 "Add file" → "Upload files",
> 把 CleanMaster 文件夹里的所有文件拖进去上传即可,效果一样。

## 如果以后本地装了 .NET SDK,也可以本地打包



```powershell
cd CleanMaster
dotnet publish -c Release -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true
```

生成的单文件位于:

```
bin\Release\net8.0-windows\win-x64\publish\CleanMaster.exe
```

这个 exe 可以直接拷贝到任意一台 Windows 10/11 电脑上双击运行,
不需要用户提前安装 .NET 运行时(因为 `--self-contained true` 已经把运行时打进去了)。

体积会比较大(通常 60-100MB 左右),这是自包含发布的正常代价,
换来的是"到哪台电脑都能跑",两者需要取舍。如果想要更小体积,
可以研究 `PublishTrimmed`,但 WPF 应用做裁剪目前兼容性一般,不建议在骨架阶段折腾。

## 关于代码签名和杀毒软件误报

清理类工具因为要批量遍历+移动文件,容易被杀毒软件的启发式引擎盯上。建议:

1. 至少做一个自签名证书签名(`signtool sign`),比完全无签名要好很多
2. 如果条件允许,申请正式的代码签名证书(如 DigiCert、Sectigo)
3. 首次发布后如果被 Windows Defender/其他杀软误报,可以提交样本到对应厂商做白名单申诉

## 目录结构说明

```
CleanMaster/
├── App.xaml / App.xaml.cs        # 全局资源(深色主题配色、卡片样式)、异常兜底
├── app.manifest                   # 声明 requireAdministrator
├── Views/
│   ├── MainWindow.xaml/.cs         # 主界面:无边框标题栏 + 三段式分类展示
│   ├── ScanItemViewModel.cs        # UI 绑定用的可变状态包装(区别于 Core 的只读数据)
│   └── Converters.cs               # XAML 绑定用的布尔值转换器
├── Core/
│   ├── ScannerEngine.cs            # 扫描逻辑,只读,不含任何 Delete/Move 调用
│   ├── CleanupRule.cs              # 规则/结果数据模型
│   ├── ProtectedPaths.cs           # 硬编码保护清单,独立于 Rules.json 的最后一道安全闸门
│   └── QuarantineManager.cs        # 清理 = 先移入隔离区(可逆),超过保留期才真正删除
└── Resources/
    └── Rules.json                  # 扫描规则配置(路径/风险等级/说明文案),后续维护只改这个文件
```

## 下一步可以扩展的方向

1. **隔离区恢复入口**:目前 `QuarantineManager.Restore()` 方法已经写好了,
   但主界面还没有暴露"查看最近清理记录 / 一键撤销"的 UI,建议尽快补上,
   这是整个安全设计里对用户最重要的一道保险。
2. **流氓软件检测**:可以新增一个 `Core/BundledSoftwareDetector.cs`,
   扫描注册表 `Uninstall` 键值和启动项,匹配已知的高频捆绑软件特征,
   引导用户走系统自带的"卸载程序"而不是暴力删文件夹。
3. **UI 美化**:建议引入 [WPF-UI](https://github.com/lepoco/wpfui) 开源库
   获得更现代的 Fluent Design 控件(圆角阴影、亚克力背景等),
   csproj 里已经预留了注释掉的 PackageReference。
4. **扫描时的路径滚动动画**:`ScannerEngine.OnProgress` 事件已经暴露了
   "当前正在扫描的路径",UI 层可以用一个轻量的滚动文字组件让扫描过程更有仪式感。
