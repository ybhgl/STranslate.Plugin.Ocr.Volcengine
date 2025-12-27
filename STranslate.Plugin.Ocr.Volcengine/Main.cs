using CommunityToolkit.Mvvm.ComponentModel;
using STranslate.Plugin.Ocr.Volcengine.View;
using STranslate.Plugin.Ocr.Volcengine.ViewModel;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;

namespace STranslate.Plugin.Ocr.Volcengine;

public class Main : ObservableObject, IOcrPlugin, ILlm
{
    private Control? _settingUi;
    private SettingsViewModel? _viewModel;
    private Settings Settings { get; set; } = null!;
    private IPluginContext Context { get; set; } = null!;

    public IEnumerable<LangEnum> SupportedLanguages => Enum.GetValues<LangEnum>();

    public ObservableCollection<Prompt> Prompts { get; set; } = [];

    public Prompt? SelectedPrompt
    {
        get => Prompts.FirstOrDefault(p => p.IsEnabled);
        set => SelectPrompt(value);
    }

    public void SelectPrompt(Prompt? prompt)
    {
        if (prompt == null) return;

        // 更新所有 Prompt 的 IsEnabled 状态
        foreach (var p in Prompts)
        {
            p.IsEnabled = p == prompt;
        }

        OnPropertyChanged(nameof(SelectedPrompt));

        // 保存到配置
        Settings.Prompts = [.. Prompts.Select(p => p.Clone())];
        Context.SaveSettingStorage<Settings>();
    }

    public Control GetSettingUI()
    {
        _viewModel ??= new SettingsViewModel(Context, Settings, this);
        _settingUi ??= new SettingsView { DataContext = _viewModel };
        return _settingUi;
    }

    public void Init(IPluginContext context)
    {
        Context = context;
        Settings = context.LoadSettingStorage<Settings>();

        // 加载 Prompt 列表
        Settings.Prompts.ForEach(Prompts.Add);
    }

    public void Dispose() => _viewModel?.Dispose();

    public string? GetLanguage(LangEnum langEnum) => null;

    public async Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken)
    {
        UriBuilder uriBuilder = new(Settings.Url);
        // 如果路径不是有效的API路径结尾，使用默认路径
        if (uriBuilder.Path == "/")
            uriBuilder.Path = "/api/v3/responses";

        var imageQuality = GetImageQualityOrDefault();

        // 处理图片数据
        var base64Str = Convert.ToBase64String(request.ImageData);
        var formatStr = imageQuality switch
        {
            ImageQuality.Low => "image/jpeg",
            ImageQuality.Medium => "image/png",
            ImageQuality.High => "image/bmp",
            _ => "image/png"
        };

        // 选择模型
        var model = Settings.Model.Trim();
        model = string.IsNullOrEmpty(model) ? "doubao-seed-1-6-vision-250815" : model;

        // 替换Prompt关键字
        var messages = (Prompts.FirstOrDefault(x => x.IsEnabled) ?? throw new Exception("请先完善Prompt配置"))
            .Clone()
            .Items;
        messages.ToList()
            .ForEach(item =>
                item.Content = item.Content.Replace("$target", ConvertLanguage(request.Language)));

        // 温度限定
        var temperature = Math.Clamp(Settings.Temperature, 0, 2);
        var userPrompt = messages.LastOrDefault() ?? throw new Exception("Prompt配置为空");
        messages.Remove(userPrompt);
        var messages2 = new List<object>();
        foreach (var item in messages)
        {
            messages2.Add(new
            {
                role = item.Role,
                content = new object[]
                {
                    new
                    {
                        type = "input_text",
                        text = item.Content
                    }
                }
            });
        }
        messages2.Add(new
        {
            role = "user",
            content = new object[]
            {
                new
                {
                    type = "input_text",
                    text = userPrompt.Content
                },
                new
                {
                    type = "input_image",
                    image_url = $"data:{formatStr};base64,{base64Str}"
                }
            }
        });

        var content = new
        {
            model,
            input = messages2.ToArray(),
            temperature,
            thinking = new
            {
                type = Settings.Thinking ? "enabled" : "disabled"
            }
        };

        var option = new Options
        {
            Headers = new Dictionary<string, string>
            {
                { "authorization", "Bearer " + Settings.ApiKey }
            }
        };

        var response = await Context.HttpService.PostAsync(uriBuilder.Uri.ToString(), content, option, cancellationToken);
        // 解析Google翻译返回的JSON
        var jsonDoc = JsonDocument.Parse(response);
        var rawData = ExtractResponseText(jsonDoc.RootElement, response);

        var result = new OcrResult();
        if (TryParseOcrContents(rawData, out var contents) && contents.Count > 0)
        {
            foreach (var item in contents)
            {
                result.OcrContents.Add(item);
            }
        }
        else
        {
            foreach (var item in rawData.Split("\n").ToList().Select(item => new OcrContent { Text = item.TrimEnd('\r') }))
            {
                result.OcrContents.Add(item);
            }
        }

        return result;
    }

    private static string ExtractResponseText(JsonElement root, string rawResponse)
    {
        if (root.TryGetProperty("output_text", out var outputText) && outputText.ValueKind == JsonValueKind.String)
            return outputText.GetString() ?? throw new Exception($"反序列化失败: {rawResponse}");

        if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
        {
            var builder = new StringBuilder();
            foreach (var outputItem in output.EnumerateArray())
            {
                if (outputItem.TryGetProperty("type", out var outputType) &&
                    outputType.ValueKind == JsonValueKind.String &&
                    outputType.GetString() == "reasoning")
                    continue;

                if (outputItem.TryGetProperty("type", out outputType) &&
                    outputType.ValueKind == JsonValueKind.String &&
                    outputType.GetString() == "message")
                {
                    if (outputItem.TryGetProperty("role", out var roleElement) &&
                        roleElement.ValueKind == JsonValueKind.String &&
                        roleElement.GetString() != "assistant")
                        continue;
                }

                if (!outputItem.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var contentItem in content.EnumerateArray())
                {
                    if (contentItem.ValueKind != JsonValueKind.Object)
                        continue;

                    if (contentItem.TryGetProperty("type", out var typeElement) &&
                        typeElement.ValueKind == JsonValueKind.String &&
                        typeElement.GetString() == "output_text" &&
                        contentItem.TryGetProperty("text", out var textElement))
                    {
                        builder.Append(textElement.GetString());
                        continue;
                    }

                    if (contentItem.TryGetProperty("text", out var fallbackText) &&
                        fallbackText.ValueKind == JsonValueKind.String)
                    {
                        builder.Append(fallbackText.GetString());
                    }
                }
            }

            if (builder.Length > 0)
                return builder.ToString();
        }

        if (root.TryGetProperty("choices", out var choices) &&
            choices.ValueKind == JsonValueKind.Array &&
            choices.GetArrayLength() > 0)
        {
            var choice = choices[0];
            if (choice.TryGetProperty("message", out var message) &&
                message.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.String)
            {
                return content.GetString() ?? throw new Exception($"反序列化失败: {rawResponse}");
            }
        }

        throw new Exception($"反序列化失败: {rawResponse}");
    }

    private ImageQuality GetImageQualityOrDefault()
    {
        var property = Context.GetType().GetProperty("ImageQuality");
        if (property?.PropertyType == typeof(ImageQuality) &&
            property.GetValue(Context) is ImageQuality quality)
        {
            return quality;
        }

        return ImageQuality.Medium;
    }

    private static bool TryParseOcrContents(string rawText, out List<OcrContent> contents)
    {
        contents = [];
        var jsonPayload = ExtractJsonPayload(rawText);
        if (!LooksLikeJson(jsonPayload))
            return false;

        try
        {
            using var jsonDoc = JsonDocument.Parse(jsonPayload);
            var root = jsonDoc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray())
                {
                    if (TryReadOcrContent(item, out var content))
                        contents.Add(content);
                }

                return contents.Count > 0;
            }

            if (root.ValueKind == JsonValueKind.Object)
            {
                if (TryReadOcrContent(root, out var single))
                    contents.Add(single);

                if (TryGetOcrArray(root, out var arrayElement))
                {
                    foreach (var item in arrayElement.EnumerateArray())
                    {
                        if (TryReadOcrContent(item, out var content))
                            contents.Add(content);
                    }
                }
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return contents.Count > 0;
    }

    private static bool TryGetOcrArray(JsonElement root, out JsonElement arrayElement)
    {
        var keys = new[]
        {
            "data",
            "items",
            "results",
            "result",
            "ocr",
            "lines",
            "blocks",
            "contents"
        };

        foreach (var key in keys)
        {
            if (root.TryGetProperty(key, out arrayElement) && arrayElement.ValueKind == JsonValueKind.Array)
                return true;
        }

        arrayElement = default;
        return false;
    }

    private static bool TryReadOcrContent(JsonElement element, out OcrContent content)
    {
        content = new OcrContent();
        if (element.ValueKind == JsonValueKind.String)
        {
            content.Text = element.GetString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(content.Text);
        }

        if (element.ValueKind != JsonValueKind.Object)
            return false;

        if (!TryGetText(element, out var text) || string.IsNullOrWhiteSpace(text))
            return false;

        content.Text = text;

        if (TryGetBoxElement(element, out var boxElement) &&
            TryParseBoxPoints(boxElement, out var points) &&
            points.Count > 0)
        {
            content.BoxPoints ??= new();
            content.BoxPoints.Clear();
            foreach (var point in points)
            {
                content.BoxPoints.Add(point);
            }
        }

        return true;
    }

    private static bool TryGetText(JsonElement element, out string? text)
    {
        var keys = new[]
        {
            "text",
            "content",
            "value",
            "line"
        };

        foreach (var key in keys)
        {
            if (element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
            {
                text = value.GetString();
                return true;
            }
        }

        text = null;
        return false;
    }

    private static bool TryGetBoxElement(JsonElement element, out JsonElement boxElement)
    {
        var keys = new[]
        {
            "box_points",
            "boxPoints",
            "box",
            "points",
            "bbox",
            "boundingBox",
            "bounding_box",
            "box_2d"
        };

        foreach (var key in keys)
        {
            if (element.TryGetProperty(key, out boxElement))
                return true;
        }

        boxElement = default;
        return false;
    }

    private static bool TryParseBoxPoints(JsonElement element, out List<BoxPoint> points)
    {
        points = [];
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (TryParsePointObject(element, out var point))
            {
                points.Add(point);
                return true;
            }

            if (TryParseRectObject(element, out var rectPoints))
            {
                points.AddRange(rectPoints);
                return true;
            }

            if (TryParseNamedVertices(element, out var vertexPoints))
            {
                points.AddRange(vertexPoints);
                return true;
            }

            return false;
        }

        if (element.ValueKind != JsonValueKind.Array)
            return false;

        var numericBuffer = new List<float>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Array)
            {
                if (TryParsePointArray(item, out var point))
                    points.Add(point);

                continue;
            }

            if (item.ValueKind == JsonValueKind.Object)
            {
                if (TryParsePointObject(item, out var point))
                    points.Add(point);

                continue;
            }

            if (TryGetFloat(item, out var value))
                numericBuffer.Add(value);
        }

        if (points.Count == 0 && numericBuffer.Count >= 2)
        {
            for (var i = 0; i + 1 < numericBuffer.Count; i += 2)
            {
                points.Add(new BoxPoint(numericBuffer[i], numericBuffer[i + 1]));
            }
        }

        return points.Count > 0;
    }

    private static bool TryParsePointArray(JsonElement element, out BoxPoint point)
    {
        point = default!;
        if (element.ValueKind != JsonValueKind.Array)
            return false;

        var coords = element.EnumerateArray().ToArray();
        if (coords.Length < 2)
            return false;

        if (!TryGetFloat(coords[0], out var x) || !TryGetFloat(coords[1], out var y))
            return false;

        point = new BoxPoint(x, y);
        return true;
    }

    private static bool TryParsePointObject(JsonElement element, out BoxPoint point)
    {
        point = default!;
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        if (element.TryGetProperty("x", out var xElement) &&
            element.TryGetProperty("y", out var yElement) &&
            TryGetFloat(xElement, out var x) &&
            TryGetFloat(yElement, out var y))
        {
            point = new BoxPoint(x, y);
            return true;
        }

        return false;
    }

    private static bool TryParseRectObject(JsonElement element, out List<BoxPoint> points)
    {
        points = [];
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        if (element.TryGetProperty("left", out var leftElement) &&
            element.TryGetProperty("top", out var topElement) &&
            element.TryGetProperty("right", out var rightElement) &&
            element.TryGetProperty("bottom", out var bottomElement) &&
            TryGetFloat(leftElement, out var left) &&
            TryGetFloat(topElement, out var top) &&
            TryGetFloat(rightElement, out var right) &&
            TryGetFloat(bottomElement, out var bottom))
        {
            points.Add(new BoxPoint(left, top));
            points.Add(new BoxPoint(right, top));
            points.Add(new BoxPoint(right, bottom));
            points.Add(new BoxPoint(left, bottom));
            return true;
        }

        return false;
    }

    private static bool TryParseNamedVertices(JsonElement element, out List<BoxPoint> points)
    {
        points = [];
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        var keys = new[]
        {
            "topLeft",
            "topRight",
            "bottomRight",
            "bottomLeft"
        };

        foreach (var key in keys)
        {
            if (!element.TryGetProperty(key, out var vertex))
                return false;

            if (vertex.ValueKind == JsonValueKind.Array && TryParsePointArray(vertex, out var arrayPoint))
            {
                points.Add(arrayPoint);
                continue;
            }

            if (vertex.ValueKind == JsonValueKind.Object && TryParsePointObject(vertex, out var objectPoint))
            {
                points.Add(objectPoint);
                continue;
            }

            return false;
        }

        return points.Count == 4;
    }

    private static bool TryGetFloat(JsonElement element, out float value)
    {
        if (element.ValueKind == JsonValueKind.Number)
        {
            if (element.TryGetSingle(out value))
                return true;

            if (element.TryGetDouble(out var doubleValue))
            {
                value = (float)doubleValue;
                return true;
            }
        }

        if (element.ValueKind == JsonValueKind.String &&
            float.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        value = 0;
        return false;
    }

    private static string ExtractJsonPayload(string rawText)
    {
        var trimmed = rawText.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
            return trimmed;

        var firstNewline = trimmed.IndexOf('\n');
        if (firstNewline < 0)
            return trimmed;

        var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        if (lastFence <= firstNewline)
            return trimmed;

        return trimmed.Substring(firstNewline + 1, lastFence - firstNewline - 1).Trim();
    }

    private static bool LooksLikeJson(string text)
        => text.StartsWith("{", StringComparison.Ordinal) || text.StartsWith("[", StringComparison.Ordinal);

    private string ConvertLanguage(LangEnum langEnum) => langEnum switch
    {
        LangEnum.Auto => "Requires you to identify automatically",
        LangEnum.ChineseSimplified => "Simplified Chinese",
        LangEnum.ChineseTraditional => "Traditional Chinese",
        LangEnum.Cantonese => "Cantonese",
        LangEnum.English => "English",
        LangEnum.Japanese => "Japanese",
        LangEnum.Korean => "Korean",
        LangEnum.French => "French",
        LangEnum.Spanish => "Spanish",
        LangEnum.Russian => "Russian",
        LangEnum.German => "German",
        LangEnum.Italian => "Italian",
        LangEnum.Turkish => "Turkish",
        LangEnum.PortuguesePortugal => "Portuguese",
        LangEnum.PortugueseBrazil => "Portuguese",
        LangEnum.Vietnamese => "Vietnamese",
        LangEnum.Indonesian => "Indonesian",
        LangEnum.Thai => "Thai",
        LangEnum.Malay => "Malay",
        LangEnum.Arabic => "Arabic",
        LangEnum.Hindi => "Hindi",
        LangEnum.MongolianCyrillic => "Mongolian",
        LangEnum.MongolianTraditional => "Mongolian",
        LangEnum.Khmer => "Central Khmer",
        LangEnum.NorwegianBokmal => "Norwegian Bokmål",
        LangEnum.NorwegianNynorsk => "Norwegian Nynorsk",
        LangEnum.Persian => "Persian",
        LangEnum.Swedish => "Swedish",
        LangEnum.Polish => "Polish",
        LangEnum.Dutch => "Dutch",
        LangEnum.Ukrainian => "Ukrainian",
        _ => "Requires you to identify automatically"
    };
}
