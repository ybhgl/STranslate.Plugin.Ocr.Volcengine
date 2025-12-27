namespace STranslate.Plugin.Ocr.Volcengine;

public class Settings
{
    public string ApiKey { get; set; } = string.Empty;
    public string Url { get; set; } = "https://ark.cn-beijing.volces.com";
    public string Model { get; set; } = "doubao-seed-1-6-vision-250815";
    public List<string> Models { get; set; } =
    [
        "doubao-seed-1-6-vision-250815",
        "doubao-seed-1-6-flash",
    ];
    public int MaxTokens { get; set; } = 2048;
    public double Temperature { get; set; } = 0.7;
    public int TopP { get; set; } = 1;
    public int N { get; set; } = 1;
    public bool Stream { get; set; } = true;
    public bool Thinking { get; set; } = false;
    public int? MaxRetries { get; set; } = 3;
    public int RetryDelayMilliseconds { get; set; } = 1000;

    public List<Prompt> Prompts { get; set; } =
    [
        new("Md & Latex",
        [
            new PromptItem("system", "你是一名严格按照要求输出 Markdown 的 OCR 模型。请对用户提供的图片进行 OCR 识别，并必须完全按照以下规则输出结果：\r\n\r\n1. 所有识别内容必须以 Markdown 文本输出。\r\n   - 若图中出现有序列表或无序列表，必须使用标准 Markdown 语法。\r\n\r\n2. 输出的换行、空格、缩进必须与原图完全一致。\r\n   - 禁止自行添加换行、空格、缩进或省略内容。\r\n   - 必须保持原图中的排版结构、层级、空格数量一致。\r\n\r\n3. 块级数学公式（独立成行的公式）必须使用 `$$ ... $$` 包裹。\r\n   示例：\r\n   $$\r\n   \\int \\sec x \\mathrm{~d}x = \\ln |\\sec x + \\tan x| + C\r\n   $$\r\n\r\n4. 行内数学公式必须使用 `$...$` 包裹。\r\n   示例：$f^{\\prime}(x_0)$\r\n\r\n5. 若图片中出现表格，必须使用标准 Markdown 表格语法输出。\r\n   - 表格内容与排版需与原图对应。\r\n\r\n6. 保持所有缩进、对齐层次与原图一致。\r\n   - 若原图包含代码块，请使用 Markdown ```code block``` 语法输出。\r\n\r\n7. 禁止使用 `\\[ ... \\]` 数学公式格式。\r\n   - 数学公式只能使用 `$...$` 或 `$$ ... $$`。\r\n\r\n8. 禁止推测、补全或修改图片内容。\r\n   - 必须原样输出所有内容，包括错别字、特殊符号或排版异常等。\r\n\r\n9. 除数学公式外，所有其他内容均以 Markdown 文本输出。\r\n\r\n请严格按照以上规则处理用户图片，不允许违反。"),
            new PromptItem("user", "请识别以下图片内容并输出 Markdown：")
        ], true),
        new("文本识别",
        [
            // https://github.com/skitsanos/gemini-ocr/blob/main/ocr.sh
            new PromptItem("user", "Act like a text scanner. Extract text as it is without analyzing it and without summarizing it. Treat all images as a whole document and analyze them accordingly. Think of it as a document with multiple pages, each image being a page. Understand page-to-page flow logically and semantically.")
        ], false),
        new("文本识别(含坐标)",
        [
            new PromptItem("user", "请识别图片中的所有文字，并严格输出 JSON 数组。每个元素包含 text 和 box_points 字段。box_points 为 4 个点组成的数组，点格式为 [x, y]，坐标基于原图像像素。只输出 JSON，不要包含多余说明。")
        ], false),
    ];
}
