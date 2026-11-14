using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Scatter;

/// <summary>
/// Рисует весь мудборд одним контролом: только те картинки, что попали в окно.
/// Для каждой картинки берёт копию подходящего размера (семь уровней детализации),
/// крупные уровни подгружает в фоне. Камеру (зум и сдвиг) получает от главного окна.
/// Порядок в списке = порядок слоёв (последняя картинка рисуется сверху).
/// </summary>
public sealed class MoodboardView : Control
{
    // Подгружаем и чуть за краями окна, чтобы при прокрутке не было пустоты
    private const double LodMargin = 300;

    private const int MaxParallelLoads = 3;

    // Сколько памяти максимум занимают подгружаемые уровни (256 и крупнее)
    private const long OnDemandBudgetBytes = 256L * 1024 * 1024;

    // Размер «уголка» для изменения размера (в экранных единицах)
    private const double HandleSize = 18;

    private static readonly IBrush SelectionBrush = new SolidColorBrush(Color.Parse("#5AA0FF"));
    private static readonly Pen SelectionPen = new(SelectionBrush, 2);
    private static readonly IBrush MarqueeFill = new SolidColorBrush(Color.Parse("#335AA0FF"));
    private static readonly Pen MarqueePen = new(SelectionBrush, 1);

    private readonly List<ImageItem> _items = new();
    private readonly HashSet<ImageItem> _selection = new();
    private readonly List<(int Stamp, int Level, ImageItem Item)> _evictable = new(); // переиспользуемый список
    private readonly SemaphoreSlim _loadGate = new(MaxParallelLoads);
    private CancellationTokenSource _cts = new();

    // Камера: экранная_точка = мировая_точка * _zoom + _offset
    private double _zoom = 1.0;
    private double _offsetX;
    private double _offsetY;

    private bool _lodSuspended;
    private int _lodStamp;
    private long _onDemandBytes;
    private Rect? _marqueeRect;

    public IReadOnlyList<ImageItem> Items => _items;

    /// <summary>Срабатывает, когда пользователь подвинул/растянул картинку или изменил порядок слоёв.</summary>
    public event Action? LayoutChanged;

    public MoodboardView()
    {
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);
    }

    /// <summary>
    /// Пока true, крупные уровни не подгружаются.
    /// Включаем на время первичной загрузки папки, чтобы не грузить лишнее.
    /// </summary>
    public bool LodSuspended
    {
        get => _lodSuspended;
        set
        {
            _lodSuspended = value;
            if (!value) UpdateLod();
        }
    }

    private double RenderScale => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;

    // ---------- Выделение ----------

    public IReadOnlyCollection<ImageItem> Selection => _selection;

    /// <summary>Картинка, на которую нажали последней. Её открывает Enter.</summary>
    public ImageItem? PrimaryItem { get; private set; }

    /// <summary>Рамка выделения в экранных координатах (рисуется, пока тянем). Null = нет рамки.</summary>
    public Rect? MarqueeRect
    {
        get => _marqueeRect;
        set
        {
            _marqueeRect = value;
            InvalidateVisual();
        }
    }

    public bool IsSelected(ImageItem item) => _selection.Contains(item);

    public void SelectOnly(ImageItem item)
    {
        _selection.Clear();
        _selection.Add(item);
        PrimaryItem = item;
        InvalidateVisual();
    }

    public void SetPrimary(ImageItem item) => PrimaryItem = item;

    /// <summary>Добавляет картинку в выделение или убирает её оттуда (Ctrl+клик).</summary>
    public void ToggleSelection(ImageItem item)
    {
        if (_selection.Remove(item))
        {
            if (ReferenceEquals(PrimaryItem, item))
                PrimaryItem = _selection.Count > 0 ? _selection.First() : null;
        }
        else
        {
            _selection.Add(item);
            PrimaryItem = item;
        }

        InvalidateVisual();
    }

    public void SetSelection(IEnumerable<ImageItem> items)
    {
        _selection.Clear();
        foreach (var item in items)
            _selection.Add(item);

        PrimaryItem = _selection.Count > 0 ? _selection.First() : null;
        InvalidateVisual();
    }

    public void SelectAll()
    {
        _selection.Clear();
        foreach (var item in _items)
            _selection.Add(item);

        PrimaryItem = _items.Count > 0 ? _items[^1] : null;
        InvalidateVisual();
    }

    public void ClearSelection()
    {
        if (_selection.Count == 0 && PrimaryItem == null) return;

        _selection.Clear();
        PrimaryItem = null;
        InvalidateVisual();
    }

    // ---------- Управление содержимым ----------

    public void AddItem(ImageItem item)
    {
        _items.Add(item);
        InvalidateVisual();
    }

    public void Clear()
    {
        // Все незавершённые загрузки отменяются
        _cts.Cancel();
        _cts = new CancellationTokenSource();

        _selection.Clear();
        PrimaryItem = null;
        _marqueeRect = null;

        foreach (var item in _items)
            item.Dispose();
        _items.Clear();
        _onDemandBytes = 0;

        InvalidateVisual();
    }

    /// <summary>Поднимает картинку наверх (рисуется поверх остальных).</summary>
    public void BringToFront(ImageItem item)
    {
        if (_items.Count == 0 || ReferenceEquals(_items[^1], item)) return;

        if (_items.Remove(item))
        {
            _items.Add(item);
            InvalidateVisual();
            LayoutChanged?.Invoke();
        }
    }

    /// <summary>Вызывать после изменения X/Y/Width/Height у картинки (или нескольких).</summary>
    public void NotifyItemChanged()
    {
        InvalidateVisual();
        UpdateLod();
        LayoutChanged?.Invoke();
    }

    public void SetCamera(double zoom, double offsetX, double offsetY)
    {
        _zoom = zoom;
        _offsetX = offsetX;
        _offsetY = offsetY;

        InvalidateVisual();
        UpdateLod();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // Размер окна изменился: набор видимых картинок другой
        if (change.Property == BoundsProperty)
            UpdateLod();
    }

    // ---------- Проверка попадания ----------

    private Rect ScreenRectOf(ImageItem item) =>
        new(item.X * _zoom + _offsetX, item.Y * _zoom + _offsetY, item.Width * _zoom, item.Height * _zoom);

    /// <summary>
    /// Находит верхнюю картинку под точкой (в экранных координатах этого контрола).
    /// isResizeHandle = true, если точка попала в правый нижний уголок.
    /// </summary>
    public ImageItem? HitTest(Point screenPoint, out bool isResizeHandle)
    {
        isResizeHandle = false;

        for (int i = _items.Count - 1; i >= 0; i--)
        {
            var item = _items[i];
            var rect = ScreenRectOf(item);

            if (!rect.Contains(screenPoint)) continue;

            double handle = Math.Min(HandleSize, Math.Min(rect.Width, rect.Height) / 3);
            isResizeHandle = screenPoint.X >= rect.Right - handle && screenPoint.Y >= rect.Bottom - handle;
            return item;
        }

        return null;
    }

    /// <summary>Все картинки, которых касается рамка (в экранных координатах).</summary>
    public List<ImageItem> ItemsIntersecting(Rect screenRect)
    {
        var result = new List<ImageItem>();
        foreach (var item in _items)
        {
            if (ScreenRectOf(item).Intersects(screenRect))
                result.Add(item);
        }

        return result;
    }

    // ---------- Отрисовка ----------

    public override void Render(DrawingContext context)
    {
        var viewport = new Rect(Bounds.Size);
        double scale = RenderScale;

        foreach (var item in _items)
        {
            var dest = ScreenRectOf(item);

            // Всё, что за пределами окна, вообще не рисуем
            if (!dest.Intersects(viewport)) continue;

            // Берём копию того размера, который нужен при текущем зуме
            var bitmap = item.GetBitmapFor(ImageLoader.LevelFor(dest.Width * scale));
            if (bitmap == null) continue;

            context.DrawImage(bitmap, new Rect(bitmap.Size), dest);
        }

        // Рамки у выделенных картинок
        foreach (var item in _selection)
        {
            var rect = ScreenRectOf(item);
            if (rect.Intersects(viewport))
                context.DrawRectangle(null, SelectionPen, rect);
        }

        // Уголок для ресайза только когда выделена ровно одна картинка
        if (_selection.Count == 1)
        {
            var rect = ScreenRectOf(_selection.First());
            if (rect.Intersects(viewport))
            {
                double handle = Math.Min(HandleSize, Math.Min(rect.Width, rect.Height) / 3);
                context.DrawRectangle(SelectionBrush, null,
                    new Rect(rect.Right - handle, rect.Bottom - handle, handle, handle));
            }
        }

        // Рамка выделения области
        if (_marqueeRect is Rect marquee)
            context.DrawRectangle(MarqueeFill, MarqueePen, marquee);
    }

    // ---------- Уровни детализации ----------

    /// <summary>
    /// Решает, какой уровень нужен каждой видимой картинке, запускает загрузку недостающих
    /// и выгружает лишнее, если памяти стало слишком много. Вызывается при любом движении камеры.
    /// Уровни 0..2 всегда в памяти, поэтому для мелких картинок ничего грузить не нужно.
    /// </summary>
    private void UpdateLod()
    {
        if (_lodSuspended || Bounds.Width <= 0 || Bounds.Height <= 0) return;

        var area = new Rect(Bounds.Size).Inflate(LodMargin);
        double scale = RenderScale;
        int stamp = ++_lodStamp;

        foreach (var item in _items)
        {
            var rect = ScreenRectOf(item);

            if (!rect.Intersects(area))
            {
                item.DesiredLevel = -1;
                continue;
            }

            item.LastNeededStamp = stamp;
            int desired = ImageLoader.LevelFor(rect.Width * scale);
            item.DesiredLevel = desired;

            if (desired >= ImageLoader.ResidentLevels &&
                !item.LoadFailed &&
                item.LoadingLevel < 0 &&
                !HasLevelInRange(item, desired, desired + 1))
            {
                StartLevelLoad(item, desired);
            }
        }

        if (_onDemandBytes > OnDemandBudgetBytes)
            EvictOnDemandLevels();
    }

    /// <summary>Есть ли у картинки загруженный уровень в диапазоне (подходит и нужный, и следующий за ним).</summary>
    private static bool HasLevelInRange(ImageItem item, int from, int to)
    {
        to = Math.Min(to, ImageLoader.LevelCount - 1);
        for (int i = from; i <= to; i++)
        {
            if (item.Levels[i] != null) return true;
        }

        return false;
    }

    private static bool IsLevelNeeded(ImageItem item, int level)
    {
        int d = item.DesiredLevel;
        return d >= 0 && level >= d && level <= d + 1;
    }

    private static long BytesOf(Bitmap bitmap) =>
        (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4;

    private void UnloadLevel(ImageItem item, int level)
    {
        var bitmap = item.Levels[level];
        if (bitmap == null) return;

        item.Levels[level] = null;
        _onDemandBytes -= BytesOf(bitmap);
        bitmap.Dispose();
    }

    /// <summary>Выгружает ненужные крупные уровни, начиная с тех, что давно не были нужны.</summary>
    private void EvictOnDemandLevels()
    {
        _evictable.Clear();

        foreach (var item in _items)
        {
            for (int level = ImageLoader.ResidentLevels; level < ImageLoader.LevelCount; level++)
            {
                if (item.Levels[level] != null && !IsLevelNeeded(item, level))
                    _evictable.Add((item.LastNeededStamp, level, item));
            }
        }

        // Сначала давно ненужные, при равенстве сначала самые крупные
        _evictable.Sort((a, b) =>
        {
            int c = a.Stamp.CompareTo(b.Stamp);
            return c != 0 ? c : b.Level.CompareTo(a.Level);
        });

        foreach (var entry in _evictable)
        {
            if (_onDemandBytes <= OnDemandBudgetBytes) break;
            UnloadLevel(entry.Item, entry.Level);
        }
    }

    private async void StartLevelLoad(ImageItem item, int level)
    {
        item.LoadingLevel = level;
        var ct = _cts.Token;

        try
        {
            await LoadLevelAsync(item, level, ct);
        }
        catch (OperationCanceledException)
        {
            // папку закрыли, это нормально
        }
        catch
        {
            item.LoadFailed = true;
        }
        finally
        {
            item.LoadingLevel = -1;
        }

        // После загрузки проверяем лимит памяти и подхватываем следующих в очереди
        if (!ct.IsCancellationRequested)
            UpdateLod();
    }

    private async Task LoadLevelAsync(ImageItem item, int level, CancellationToken ct)
    {
        await _loadGate.WaitAsync(ct);
        try
        {
            // Пока стояли в очереди, картинка могла уехать за край или зум мог измениться
            if (item.IsDisposed || !IsLevelNeeded(item, level))
                return;

            var bitmap = await Task.Run(() => ImageLoader.TryDecode(item.FilePath, ImageLoader.LevelWidths[level]), ct);

            if (bitmap == null)
            {
                item.LoadFailed = true;
                return;
            }

            if (ct.IsCancellationRequested || item.IsDisposed)
            {
                bitmap.Dispose();
                return;
            }

            UnloadLevel(item, level); // на случай, если такой уровень уже был
            item.Levels[level] = bitmap;
            _onDemandBytes += BytesOf(bitmap);
            InvalidateVisual();
        }
        finally
        {
            _loadGate.Release();
        }
    }
}