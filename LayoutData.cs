using System.Collections.Generic;

namespace Scatter;

/// <summary>Содержимое файла .scatter.json.</summary>
public sealed class LayoutData
{
    public int Version { get; set; } = 1;

    /// <summary>Где была камера. Может отсутствовать.</summary>
    public CameraData? Camera { get; set; }

    /// <summary>Порядок в списке = порядок слоёв (последняя картинка лежит сверху).</summary>
    public List<ItemData> Items { get; set; } = new();
}

/// <summary>
/// Камера хранится как «центр окна в мировых координатах + зум».
/// Так она не зависит от размера окна, в котором папку открыли в прошлый раз.
/// </summary>
public sealed class CameraData
{
    public double Zoom { get; set; } = 1.0;
    public double CenterX { get; set; }
    public double CenterY { get; set; }
}

public sealed class ItemData
{
    /// <summary>Имя файла внутри папки (путь относительный, так что папку можно переносить).</summary>
    public string File { get; set; } = "";

    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }
}