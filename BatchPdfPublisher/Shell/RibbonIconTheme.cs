using System;

namespace BatchPdfPublisher.Services
{
    /// <summary>
    /// 功能区图标的配色与字形规则。
    ///
    /// 原来的图标是同一支细蓝线画的小图形，16 px 下几乎分不出来。改成
    /// **按分组着色的圆角色块 + 一个白色汉字**：颜色一眼分大类，汉字一眼分具体功能，
    /// 而且汉字在 16 px 下比线条图形清楚得多。
    ///
    /// 不依赖 AutoCAD / WPF，可离线断言——颜色只以十六进制字符串表达。
    /// </summary>
    public static class RibbonIconTheme
    {
        private const string FallbackColor = "#2D70BE";
        private const string FallbackGlyph = "•";

        /// <summary>按分组取底色。图纸类蓝、建筑类青、制图图层类橙绿、系统类灰。</summary>
        public static string ColorHex(string group)
        {
            var name = group ?? string.Empty;
            if (name.StartsWith("图层工具", StringComparison.Ordinal)) return "#1F7A3D";
            switch (name)
            {
                case "图纸与发布": return "#2D70BE";
                case "图块与属性": return "#6A4FBF";
                case "建筑工具": return "#0E8A7D";
                case "制图与标注": return "#B8730F";
                case "系统设置": return "#5A6472";
                default: return FallbackColor;
            }
        }

        /// <summary>
        /// 取图标上的汉字。登记表里 <c>icon</c> 字段直接放一个字（例如「梯」「窗」），
        /// 每个功能尽量不重样；为空时给一个中性符号，避免出现空图标。
        /// </summary>
        public static string GlyphFor(string icon)
        {
            var text = (icon ?? string.Empty).Trim();
            if (text.Length == 0) return FallbackGlyph;
            // 按文本元素取第一个字符，避免把代理对（少见字符）截断。
            var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(text);
            return enumerator.MoveNext() ? Convert.ToString(enumerator.GetTextElement()) : FallbackGlyph;
        }
    }
}
