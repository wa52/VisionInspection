using System.Windows;

namespace SpeakerVisionInspection;

/// <summary>WPF UI 辅助扩展。</summary>
public static class UiExtensions
{
    /// <summary>初始化后返回自身（流畅式 UI 构建）。</summary>
    public static T Apply<T>(this T element, Action<T> init) where T : System.Windows.UIElement
    {
        init(element);
        return element;
    }
}
