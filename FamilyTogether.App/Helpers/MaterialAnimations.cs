using Microsoft.Maui.Controls;

namespace FamilyTogether.App.Helpers;

public static class MaterialAnimations
{
    // Duraciones estándar Material Design
    public const uint FastDuration = 200;
    public const uint MediumDuration = 300;
    public const uint SlowDuration = 500;
    public const uint ExtraSlowDuration = 700;

    // Curvas de animación Material Design
    public static readonly Easing StandardEasing = Easing.CubicOut;
    public static readonly Easing AccelerateEasing = Easing.CubicIn;
    public static readonly Easing DecelerateEasing = Easing.CubicOut;
    public static readonly Easing EmphasizedEasing = new Easing(0.2, 0.0, 0, 1.0);

    /// <summary>
    /// Animación de entrada fade in con slide up
    /// </summary>
    public static async Task FadeInSlideUpAsync(VisualElement element, uint duration = MediumDuration, int startOffset = 50)
    {
        element.Opacity = 0;
        element.TranslationY = startOffset;
        
        await Task.WhenAll(
            element.FadeTo(1, duration, StandardEasing),
            element.TranslateTo(0, 0, duration, StandardEasing)
        );
    }

    /// <summary>
    /// Animación de salida fade out con slide down
    /// </summary>
    public static async Task FadeOutSlideDownAsync(VisualElement element, uint duration = MediumDuration, int endOffset = 50)
    {
        await Task.WhenAll(
            element.FadeTo(0, duration, AccelerateEasing),
            element.TranslateTo(0, endOffset, duration, AccelerateEasing)
        );
    }

    /// <summary>
    /// Animación de entrada secuencial para múltiples elementos
    /// </summary>
    public static async Task StaggeredEntranceAsync(IEnumerable<VisualElement> elements, uint delayBetween = 100, uint duration = MediumDuration)
    {
        var tasks = new List<Task>();
        var delay = 0;

        foreach (var element in elements)
        {
            var currentDelay = delay;
            tasks.Add(Task.Run(async () =>
            {
                await Task.Delay((int)currentDelay);
                await FadeInSlideUpAsync(element, duration);
            }));
            delay += (int)delayBetween;
        }

        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Animación de botón presionado Material Design
    /// </summary>
    public static async Task ButtonPressAsync(Button button)
    {
        await button.ScaleTo(0.95, FastDuration, AccelerateEasing);
        await button.ScaleTo(1.0, FastDuration, DecelerateEasing);
    }

    /// <summary>
    /// Animación de card hover/tap
    /// </summary>
    public static async Task CardElevationAsync(VisualElement card, bool elevate = true)
    {
        if (elevate)
        {
            await Task.WhenAll(
                card.ScaleTo(1.02, FastDuration, StandardEasing),
                card.TranslateTo(0, -2, FastDuration, StandardEasing)
            );
        }
        else
        {
            await Task.WhenAll(
                card.ScaleTo(1.0, FastDuration, StandardEasing),
                card.TranslateTo(0, 0, FastDuration, StandardEasing)
            );
        }
    }

    /// <summary>
    /// Animación de ripple effect simulado
    /// </summary>
    public static async Task RippleEffectAsync(VisualElement element)
    {
        var originalOpacity = element.Opacity;
        
        await element.FadeTo(0.7, FastDuration / 2, AccelerateEasing);
        await element.FadeTo(originalOpacity, FastDuration / 2, DecelerateEasing);
    }

    /// <summary>
    /// Animación de shake para errores
    /// </summary>
    public static async Task ShakeAsync(VisualElement element, int amplitude = 10, uint duration = FastDuration)
    {
        var originalX = element.TranslationX;
        
        await element.TranslateTo(originalX + amplitude, element.TranslationY, duration / 4);
        await element.TranslateTo(originalX - amplitude, element.TranslationY, duration / 2);
        await element.TranslateTo(originalX, element.TranslationY, duration / 4);
    }

    /// <summary>
    /// Animación de bounce para confirmaciones
    /// </summary>
    public static async Task BounceAsync(VisualElement element, double scale = 1.2, uint duration = MediumDuration)
    {
        await element.ScaleTo(scale, duration / 2, DecelerateEasing);
        await element.ScaleTo(1.0, duration / 2, StandardEasing);
    }

    /// <summary>
    /// Animación de loading spinner personalizada
    /// </summary>
    public static async Task SpinAsync(VisualElement element, uint duration = 1000, int rotations = 1)
    {
        await element.RotateTo(360 * rotations, duration, Easing.Linear);
        element.Rotation = 0; // Reset rotation
    }

    /// <summary>
    /// Animación de entrada de página completa
    /// </summary>
    public static async Task PageEntranceAsync(ContentPage page)
    {
        if (page.Content != null)
        {
            page.Content.Opacity = 0;
            page.Content.TranslationY = 30;
            
            await Task.WhenAll(
                page.Content.FadeTo(1, SlowDuration, StandardEasing),
                page.Content.TranslateTo(0, 0, SlowDuration, StandardEasing)
            );
        }
    }

    /// <summary>
    /// Animación de transición entre estados
    /// </summary>
    public static async Task CrossFadeAsync(VisualElement elementOut, VisualElement elementIn, uint duration = MediumDuration)
    {
        elementIn.Opacity = 0;
        elementIn.IsVisible = true;
        
        await Task.WhenAll(
            elementOut.FadeTo(0, duration, StandardEasing),
            elementIn.FadeTo(1, duration, StandardEasing)
        );
        
        elementOut.IsVisible = false;
    }
}