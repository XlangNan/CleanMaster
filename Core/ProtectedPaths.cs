using System;
using System.Collections.Generic;
using System.Linq;

namespace CleanMaster.Core
{
    /// <summary>
    /// 硬编码的"绝对不碰"路径/文件名清单。
    ///
    /// 设计原则:这里的判断逻辑独立于 Rules.json 之外,作为最后一道安全闸门——
    /// 即便未来有人在 Rules.json 里配错了路径(比如不小心把整个 WeChat Files
    /// 目录当成"缓存"配进去),ScannerEngine 在展开每一个具体路径之前都会
    /// 先过这一层检查,命中就直接跳过、不生成结果条目、不计入统计。
    ///
    /// 这不是配置文件,是有意做成代码硬编码 —— 避免被外部配置意外覆盖。
    /// </summary>
    public static class ProtectedPaths
    {
        // 整个目录都不进入扫描范围(子目录/子文件一律跳过)
        public static readonly string[] ProtectedDirectoryNames =
        {
            // 微信 / QQ / TIM 用户数据目录(聊天记录、图片、语音、视频都在这里)
            "WeChat Files",
            "Tencent Files",
            "TIM Files",

            // 企业微信 / 钉钉
            "WXWork",
            "DingTalk",

            // 用户个人文档类目录,防止误配规则时波及
            "Documents",
            "Desktop",
            "Pictures",
            "Videos",
            "Music",
        };

        // 具体文件名/文件名模式,即使所在目录被判定为"缓存目录"也要单独排除
        public static readonly string[] ProtectedFileNames =
        {
            // 浏览器登录态/密码/自动填充数据库(Chromium 内核系列通用文件名)
            "Login Data",
            "Login Data For Account",
            "Cookies",
            "Web Data",
            "History",
            "Bookmarks",
            "Preferences",

            // 各类客户端授权/许可证文件常见命名
            "license.dat",
            "auth.json",
            "credentials.json",
        };

        /// <summary>
        /// 判断某个绝对路径是否命中保护清单(目录名或文件名任意一段匹配即命中)。
        /// </summary>
        public static bool IsProtected(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath)) return true; // 空路径一律当危险处理

            var segments = fullPath.Split(
                new[] { '\\', '/' },
                StringSplitOptions.RemoveEmptyEntries);

            foreach (var segment in segments)
            {
                if (ProtectedDirectoryNames.Any(p =>
                        string.Equals(p, segment, StringComparison.OrdinalIgnoreCase)))
                    return true;

                if (ProtectedFileNames.Any(p =>
                        string.Equals(p, segment, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// 用于 UI 上"本工具不会触碰"这一栏的静态展示文案,与实际拦截逻辑分开维护，
        /// 纯粹是给用户看的说明，不参与判断。
        /// </summary>
        public static readonly List<string> UserFacingExclusionNotes = new()
        {
            "微信 / QQ / 企业微信 / 钉钉等聊天软件的用户数据目录(聊天记录、图片、语音、视频)",
            "浏览器的登录状态、密码、Cookie、自动填充数据库",
            "桌面 / 文档 / 图片 / 视频 / 音乐等个人文件目录",
            "任何软件的授权文件、许可证文件、配置文件",
        };
    }
}
