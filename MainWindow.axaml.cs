using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace Scatter;

public partial class MainWindow : Window
{
    // ---------- Настройки ----------

    private const double MinZoom = 0.02;
    private const double MaxZoom = 8.0;
    private const double WheelStep = 60;       // пикселей за один «щелчок» колеса

    private const double ColumnWidth = 256;    // ширина картинки на холсте (мировые единицы)
    private const double ColumnGap = 16;
    private const double MinItemWidth = 32;    // меньше этого картинку уменьшить нельзя
    private const double NewItemsGap = 64;     // отступ новых картинок от уже расставленных

    private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(2);

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp"
    };

    // Файл из папки + сохранённое для него место (null = в раскладке такого файла нет)
    private readonly record struct Entry(string Path, Rect? Saved);

    // Постоянные уровни детализации одной картинки (32, 64 и 128 px)
    private sealed record Decoded(Bitmap Thumb, Bitmap? Small, Bitmap? Mini)
    {
        public void Dispose()
        {
            Thumb.Dispose();
            Small?.Dispose();
            Mini?.Dispose();
        }
    }

    // ---------- Состояние ----------

    // Камера: экранная_точка = мировая_точка * _zoom + _offset
    // (Point в Avalonia неизменяемый, поэтому смещение хранится двумя числами)
    private double _zoom = 1.0;
    private double _offsetX;
    private double _offsetY;

    // Что сейчас делает мышь
    private enum DragMode { None, Pan, Move, Resize, Marquee }
    private DragMode _mode = DragMode.None;

    private Point _panLast;                 // для панорамирования
    private Point _pressScreen;             // где нажали (для перемещения, ресайза, рамки)
    private ImageItem? _activeItem;         // какую картинку тащим/растягиваем
    private double _startX, _startY, _startW, _startH;

    // Стартовые позиции всех выделенных картинок при групповом переносе
    private readonly List<(ImageItem Item, double X, double Y)> _groupStart = new();

    // Что было выделено до начала рамки (непусто только при Ctrl+рамке)
    private readonly HashSet<ImageItem> _marqueeBase = new();

    private readonly Cursor _arrowCursor = new(StandardCursorType.Arrow);
    private readonly Cursor _handCursor = new(StandardCursorType.Hand);
    private readonly Cursor _moveCursor = new(StandardCursorType.SizeAll);
    private readonly Cursor _resizeCursor = new(StandardCursorType.BottomRightCorner);

    private CancellationTokenSource? _loadCts;
    private string? _currentFolder;

    private readonly AppSettings _settings = SettingsStore.Load();

    // Сохранение
    private readonly DispatcherTimer _saveTimer;
    private bool _layoutReady;    // папка полностью загружена, можно сохранять
    private bool _layoutTouched;  // раскладка уже «живая»: есть файл или пользователь что-то менял
    private bool _dirty;          // есть несохранённые изменения

    // Полноэкранный просмотр
    private readonly List<ImageItem> _viewerItems = new();
    private int _viewerIndex;
    private CancellationTokenSource? _viewerCts;
    private Bitmap? _viewerBitmap; // копия под размер окна, которой владеем мы (её нужно освобождать)

    public MainWindow()
    {
        InitializeComponent();

        Viewport.PointerPressed += OnPointerPressed;
        Viewport.PointerMoved += OnPointerMoved;
        Viewport.PointerReleased += OnPointerReleased;
        Viewport.PointerCaptureLost += OnPointerCaptureLost;
        Viewport.PointerWheelChanged += OnPointerWheelChanged;

        ViewerScreen.PointerPressed += OnViewerPointerPressed;

        KeyDown += OnKeyDown;

        _saveTimer = new DispatcherTimer { Interval = SaveDelay };
        _saveTimer.Tick += (_, _) => SaveLayoutNow();

        Board.LayoutChanged += () => MarkDirty(itemsChanged: true);

        Closing += (_, _) =>
        {
            _loadCts?.Cancel();
            SaveLayoutNow();
        };

        ApplyCamera();
        ShowStartScreen();
    }

    // =====================================================================
    //  Открытие папки и загрузка картинок
    // =====================================================================

    private async void OnOpenFolderClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Выбери папку с картинками",
                AllowMultiple = false
            });

            if (folders.Count == 0) return;

            var path = folders[0].TryGetLocalPath();
            if (path == null)
            {
                StatusText.Text = "Не удалось получить путь к папке";
                return;
            }

            await OpenFolderAsync(path);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Ошибка: " + ex.Message;
        }
    }

    // =====================================================================
    //  Стартовый экран и список последних папок
    // =====================================================================

    /// <summary>Возвращается к списку: сохраняет раскладку, освобождает память от картинок.</summary>
    private void ShowStartScreen()
    {
        CloseViewer();
        SaveLayoutNow();
        _loadCts?.Cancel();

        _layoutReady = false;
        _layoutTouched = false;
        _dirty = false;

        Board.Clear();
        _currentFolder = null;

        Title = "Scatter";
        StatusText.Text = "Папка не выбрана";
        SaveText.Text = "";
        StartMessage.Text = "";

        RebuildRecentList();
        StartScreen.IsVisible = true;
    }

    private void OnBackClick(object? sender, RoutedEventArgs e) => ShowStartScreen();

    /// <summary>Общий вход для «Открыть папку…» и для клика по списку.</summary>
    private async Task OpenFolderAsync(string path)
    {
        if (!Directory.Exists(path))
        {
            SettingsStore.Remove(_settings, path);
            SettingsStore.Save(_settings);
            RebuildRecentList();
            StartMessage.Text = "Папка не найдена: " + path;
            StartScreen.IsVisible = true;
            return;
        }

        StartMessage.Text = "";
        SettingsStore.AddRecent(_settings, path);
        SettingsStore.Save(_settings);

        StartScreen.IsVisible = false;
        await LoadFolderAsync(path);
    }

    private async void OpenRecent(string path)
    {
        try
        {
            await OpenFolderAsync(path);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Ошибка: " + ex.Message;
        }
    }

    private void RebuildRecentList()
    {
        // Папки, которых больше нет, пропадают из списка
        int before = _settings.RecentFolders.Count;
        _settings.RecentFolders.RemoveAll(p => !Directory.Exists(p));
        if (_settings.RecentFolders.Count != before)
            SettingsStore.Save(_settings);

        RecentList.Children.Clear();

        foreach (var path in _settings.RecentFolders.ToList())
        {
            var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(name)) name = path; // корень диска

            var openButton = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(14, 10),
                Content = new StackPanel
                {
                    Spacing = 2,
                    Children =
                    {
                        new TextBlock { Text = name, FontSize = 15, FontWeight = FontWeight.SemiBold },
                        new TextBlock
                        {
                            Text = path,
                            FontSize = 12,
                            Opacity = 0.6,
                            TextTrimming = TextTrimming.CharacterEllipsis
                        }
                    }
                }
            };
            ToolTip.SetTip(openButton, path);

            var folderPath = path; // копия для обработчиков
            openButton.Click += (_, _) => OpenRecent(folderPath);

            var removeButton = new Button
            {
                Content = "✕",
                VerticalAlignment = VerticalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0),
                Padding = new Thickness(12, 0)
            };
            ToolTip.SetTip(removeButton, "Убрать из списка");
            removeButton.Click += (_, _) =>
            {
                SettingsStore.Remove(_settings, folderPath);
                SettingsStore.Save(_settings);
                RebuildRecentList();
            };

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Grid.SetColumn(removeButton, 1);
            row.Children.Add(openButton);
            row.Children.Add(removeButton);

            RecentList.Children.Add(row);
        }

        bool hasRecent = RecentList.Children.Count > 0;
        RecentHeader.IsVisible = hasRecent;
        RecentEmpty.IsVisible = !hasRecent;
    }

    private async Task LoadFolderAsync(string folder)
    {
        // Сначала сохраняем то, что было в прошлой папке
        SaveLayoutNow();
        _layoutReady = false;
        _layoutTouched = false;
        _dirty = false;
        SaveText.Text = "";

        // Отменяем предыдущую загрузку (если ещё идёт) и очищаем холст
        _loadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        var ct = cts.Token;

        Board.LodSuspended = true; // на время загрузки крупные уровни не нужны
        Board.Clear();
        ResetCamera();

        _currentFolder = folder;
        var folderName = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        Title = "Scatter — " + folderName;

        // 1. Список файлов (только сама папка, без вложенных) и сохранённая раскладка
        StatusText.Text = "Читаю папку…";
        var files = await Task.Run(() =>
            Directory.EnumerateFiles(folder)
                .Where(f => !Path.GetFileName(f).StartsWith('.') &&
                            ImageExtensions.Contains(Path.GetExtension(f)))
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .ToList());

        var layoutData = await Task.Run(() => LayoutStore.Load(folder));

        if (ct.IsCancellationRequested) return;

        if (files.Count == 0)
        {
            Board.LodSuspended = false;
            StatusText.Text = "В папке нет картинок";
            return;
        }

        // 2. Порядок загрузки: сначала файлы из раскладки в порядке слоёв (снизу вверх),
        //    потом новые файлы, которых в раскладке ещё нет. Раз картинки добавляются
        //    на холст именно в этом порядке, порядок слоёв восстанавливается сам собой.
        var comparer = LayoutStore.FileNameComparer;
        var byName = new Dictionary<string, string>(comparer);
        foreach (var f in files) byName[Path.GetFileName(f)] = f;

        var entries = new List<Entry>(files.Count);
        var used = new HashSet<string>(comparer);

        if (layoutData != null)
        {
            foreach (var it in layoutData.Items)
            {
                if (string.IsNullOrEmpty(it.File)) continue;
                if (!byName.TryGetValue(it.File, out var path)) continue; // файл удалили
                if (!used.Add(it.File)) continue;                         // дубль в файле

                bool valid = double.IsFinite(it.X) && double.IsFinite(it.Y) &&
                             double.IsFinite(it.W) && double.IsFinite(it.H) &&
                             it.W > 0 && it.H > 0;

                Rect? saved = valid ? new Rect(it.X, it.Y, it.W, it.H) : null;
                entries.Add(new Entry(path, saved));
            }
        }

        foreach (var f in files)
        {
            if (!used.Contains(Path.GetFileName(f)))
                entries.Add(new Entry(f, null));
        }

        // Новые картинки кладём колонками под уже расставленными
        int newCount = entries.Count(e => e.Saved == null);
        double originX = 0, originY = 0;
        var savedRects = entries.Where(e => e.Saved != null).Select(e => e.Saved!.Value).ToList();
        if (savedRects.Count > 0)
        {
            originX = savedRects.Min(r => r.X);
            originY = savedRects.Max(r => r.Bottom) + NewItemsGap;
        }
        var origin = new Point(originX, originY);

        var layout = new MasonryLayout(MasonryLayout.SuggestColumns(Math.Max(1, newCount)), ColumnWidth, ColumnGap);

        // 3. Параллельная декодировка постоянных уровней (128, 64 и 32 px).
        //    Результаты ставятся на холст строго в порядке entries, даже если декодировались вразнобой.
        int total = entries.Count;
        var decodedItems = new Decoded?[total];
        var finished = new bool[total];
        int nextToPlace = 0;
        int failed = 0;

        StatusText.Text = $"Загрузка: 0 / {total}";

        try
        {
            await Task.Run(async () =>
            {
                var options = new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2),
                    CancellationToken = ct
                };

                await Parallel.ForEachAsync(Enumerable.Range(0, total), options, async (i, token) =>
                {
                    Decoded? decoded = null;
                    var thumb = ImageLoader.TryDecode(entries[i].Path, ImageLoader.LevelWidths[ImageLoader.ThumbLevel]);
                    if (thumb != null)
                    {
                        // Мелкие уровни делаем из уже готового, без повторного чтения файла
                        var small = ImageLoader.TryScale(thumb, ImageLoader.LevelWidths[1]);
                        var mini = ImageLoader.TryScale(small ?? thumb, ImageLoader.LevelWidths[0]);
                        decoded = new Decoded(thumb, small, mini);
                    }

                    // Дальше всё на UI-потоке, поэтому блокировки не нужны
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (ct.IsCancellationRequested)
                        {
                            decoded?.Dispose();
                            return;
                        }

                        decodedItems[i] = decoded;
                        finished[i] = true;
                        if (decoded == null) failed++;

                        while (nextToPlace < total && finished[nextToPlace])
                        {
                            var ready = decodedItems[nextToPlace];
                            if (ready != null)
                            {
                                var entry = entries[nextToPlace];
                                PlaceItem(entry.Path, ready, entry.Saved, layout, origin);
                            }
                            decodedItems[nextToPlace] = null;
                            nextToPlace++;
                        }

                        StatusText.Text = $"Загрузка: {nextToPlace} / {total}";
                    });
                });
            }, ct);
        }
        catch (OperationCanceledException)
        {
            return; // открыли другую папку
        }

        if (ct.IsCancellationRequested) return;

        // 4. Готово: камера, подгрузка крупных уровней, разрешаем сохранение
        if (layoutData?.Camera != null)
            RestoreCamera(layoutData.Camera);
        else
            FitToContent();

        Board.LodSuspended = false;

        int added = layoutData != null ? newCount : 0;
        StatusText.Text = $"{folderName} — {Board.Items.Count} картинок" +
                          (added > 0 ? $", новых: {added}" : "") +
                          (failed > 0 ? $" (не удалось открыть: {failed})" : "");

        // Если файл раскладки уже есть, дальше сохраняем и положение камеры.
        // Если папку открыли впервые, файл не создаём, пока пользователь ничего не изменит.
        _layoutTouched = layoutData != null;
        _dirty = false;
        _layoutReady = true;
    }

    private void PlaceItem(string path, Decoded decoded, Rect? saved, MasonryLayout layout, Point origin)
    {
        Rect rect;
        if (saved is Rect s)
        {
            rect = s;
        }
        else
        {
            double aspect = decoded.Thumb.PixelSize.Height > 0
                ? (double)decoded.Thumb.PixelSize.Width / decoded.Thumb.PixelSize.Height
                : 1.0;

            var r = layout.Add(aspect);
            rect = new Rect(r.X + origin.X, r.Y + origin.Y, r.Width, r.Height);
        }

        Board.AddItem(new ImageItem
        {
            FilePath = path,
            Thumb = decoded.Thumb,
            Small = decoded.Small,
            Mini = decoded.Mini,
            X = rect.X,
            Y = rect.Y,
            Width = rect.Width,
            Height = rect.Height
        });
    }

    // =====================================================================
    //  Сохранение раскладки
    // =====================================================================

    private void MarkDirty(bool itemsChanged)
    {
        if (!_layoutReady) return;

        if (itemsChanged) _layoutTouched = true;
        if (!_layoutTouched) return;

        _dirty = true;
        _saveTimer.Stop();   // перезапуск: сохраняем через 2 секунды после последнего изменения
        _saveTimer.Start();
    }

    private void SaveLayoutNow()
    {
        _saveTimer.Stop();
        if (!_dirty || !_layoutReady || _currentFolder == null) return;

        var result = LayoutStore.Save(_currentFolder, BuildLayoutData());

        switch (result)
        {
            case SaveResult.SavedToFolder:
                _dirty = false;
                SaveText.Text = "Сохранено " + DateTime.Now.ToString("HH:mm:ss");
                break;

            case SaveResult.SavedToAppData:
                _dirty = false;
                SaveText.Text = "Сохранено в AppData (в папку писать нельзя)";
                break;

            default:
                SaveText.Text = "Не удалось сохранить раскладку";
                break;
        }
    }

    private LayoutData BuildLayoutData()
    {
        static double R(double v, int digits = 2) => Math.Round(v, digits);

        var data = new LayoutData();

        double viewW = Viewport.Bounds.Width;
        double viewH = Viewport.Bounds.Height;
        if (viewW > 0 && viewH > 0 && _zoom > 0)
        {
            data.Camera = new CameraData
            {
                Zoom = R(_zoom, 4),
                CenterX = R((viewW / 2 - _offsetX) / _zoom),
                CenterY = R((viewH / 2 - _offsetY) / _zoom)
            };
        }

        foreach (var item in Board.Items)
        {
            data.Items.Add(new ItemData
            {
                File = item.FileName,
                X = R(item.X),
                Y = R(item.Y),
                W = R(item.Width),
                H = R(item.Height)
            });
        }

        return data;
    }

    // =====================================================================
    //  Камера
    // =====================================================================

    private void ApplyCamera()
    {
        Board.SetCamera(_zoom, _offsetX, _offsetY);
        GridLayer.SetCamera(_zoom, _offsetX, _offsetY);

        ZoomText.Text = $"{Math.Round(_zoom * 100)}%   " +
                        "Ctrl+колесо — зум · ПКМ — двигать холст · рамка / Ctrl+клик — выделение · " +
                        "двойной клик — просмотр · F — показать всё · Shift — сетка";

        MarkDirty(itemsChanged: false);
    }

    private void ZoomAt(Point screenPoint, double factor)
    {
        double newZoom = Math.Clamp(_zoom * factor, MinZoom, MaxZoom);
        double actual = newZoom / _zoom; // фактический множитель с учётом ограничений
        if (Math.Abs(actual - 1.0) < 1e-9) return;

        // Точка под курсором остаётся на месте
        _offsetX = screenPoint.X - (screenPoint.X - _offsetX) * actual;
        _offsetY = screenPoint.Y - (screenPoint.Y - _offsetY) * actual;
        _zoom = newZoom;

        ApplyCamera();
    }

    private void ResetCamera()
    {
        _zoom = 1.0;
        _offsetX = 0;
        _offsetY = 0;
        ApplyCamera();
    }

    /// <summary>Возвращает камеру, как она была при последнем сохранении.</summary>
    private void RestoreCamera(CameraData cam)
    {
        double viewW = Viewport.Bounds.Width;
        double viewH = Viewport.Bounds.Height;

        if (viewW <= 0 || viewH <= 0 ||
            !double.IsFinite(cam.Zoom) || !double.IsFinite(cam.CenterX) || !double.IsFinite(cam.CenterY) ||
            cam.Zoom <= 0)
        {
            FitToContent();
            return;
        }

        _zoom = Math.Clamp(cam.Zoom, MinZoom, MaxZoom);
        _offsetX = viewW / 2 - cam.CenterX * _zoom;
        _offsetY = viewH / 2 - cam.CenterY * _zoom;
        ApplyCamera();
    }

    /// <summary>Подбирает зум и сдвиг так, чтобы все картинки поместились в окно.</summary>
    private void FitToContent()
    {
        var items = Board.Items;
        if (items.Count == 0)
        {
            ResetCamera();
            return;
        }

        double viewW = Viewport.Bounds.Width;
        double viewH = Viewport.Bounds.Height;
        if (viewW <= 0 || viewH <= 0) return;

        double minX = items.Min(i => i.X);
        double minY = items.Min(i => i.Y);
        double maxX = items.Max(i => i.X + i.Width);
        double maxY = items.Max(i => i.Y + i.Height);
        double contentW = maxX - minX;
        double contentH = maxY - minY;

        const double padding = 48;
        double zoom = Math.Min(
            Math.Max(1, viewW - 2 * padding) / contentW,
            Math.Max(1, viewH - 2 * padding) / contentH);

        _zoom = Math.Clamp(zoom, MinZoom, 1.0); // не увеличиваем больше 100%
        _offsetX = (viewW - contentW * _zoom) / 2 - minX * _zoom;
        _offsetY = (viewH - contentH * _zoom) / 2 - minY * _zoom;

        ApplyCamera();
    }

    // =====================================================================
    //  Ввод мышью на холсте
    // =====================================================================

    private static double Snap(double value) =>
        Math.Round(value / GridBackground.CellSize) * GridBackground.CellSize;

    private void SetCursor(Cursor cursor)
    {
        if (!ReferenceEquals(Viewport.Cursor, cursor))
            Viewport.Cursor = cursor;
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_mode != DragMode.None) return; // уже что-то делаем другой кнопкой

        var props = e.GetCurrentPoint(Viewport).Properties;
        bool left = props.IsLeftButtonPressed;
        bool middle = props.IsMiddleButtonPressed;
        bool right = props.IsRightButtonPressed;
        if (!left && !middle && !right) return;

        var pos = e.GetPosition(Viewport);
        bool ctrl = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;

        if (left)
        {
            var hit = Board.HitTest(pos, out bool isHandle);

            if (hit != null)
            {
                // Двойной клик: полноэкранный просмотр
                if (e.ClickCount == 2 && !isHandle)
                {
                    if (!Board.IsSelected(hit)) Board.SelectOnly(hit);
                    else Board.SetPrimary(hit);

                    OpenViewer(hit);
                    e.Handled = true;
                    return;
                }

                // Ctrl+клик: добавить в выделение или убрать из него
                if (ctrl)
                {
                    Board.ToggleSelection(hit);
                    e.Handled = true;
                    return;
                }

                // Обычный клик: поднимаем наверх. Если картинка уже в выделенной группе,
                // группа остаётся (чтобы её можно было тащить целиком), иначе выделяем только её.
                Board.BringToFront(hit);
                if (!Board.IsSelected(hit)) Board.SelectOnly(hit);
                else Board.SetPrimary(hit);

                _activeItem = hit;
                _pressScreen = pos;
                _startX = hit.X;
                _startY = hit.Y;
                _startW = hit.Width;
                _startH = hit.Height;

                // Растягивать можно только одну картинку. Если выделено несколько,
                // уголок работает как обычная часть картинки и тащит всю группу.
                bool resize = isHandle && Board.Selection.Count == 1;
                if (resize)
                {
                    _mode = DragMode.Resize;
                }
                else
                {
                    _groupStart.Clear();
                    foreach (var item in Board.Selection)
                        _groupStart.Add((item, item.X, item.Y));
                    _mode = DragMode.Move;
                }

                SetCursor(resize ? _resizeCursor : _moveCursor);
                e.Pointer.Capture(Viewport);
                e.Handled = true;
                return;
            }

            // Пустое место: тянем рамку выделения.
            // С Ctrl рамка добавляет к уже выделенному, без Ctrl выделение начинается заново.
            _marqueeBase.Clear();
            if (ctrl)
            {
                foreach (var item in Board.Selection)
                    _marqueeBase.Add(item);
            }
            else
            {
                Board.ClearSelection();
            }

            _pressScreen = pos;
            _mode = DragMode.Marquee;
            e.Pointer.Capture(Viewport);
            e.Handled = true;
            return;
        }

        // Правая и средняя кнопки двигают холст, даже над картинкой
        _mode = DragMode.Pan;
        _panLast = pos;
        SetCursor(_moveCursor);
        e.Pointer.Capture(Viewport);
        e.Handled = true;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        var pos = e.GetPosition(Viewport);

        switch (_mode)
        {
            case DragMode.Pan:
                _offsetX += pos.X - _panLast.X;
                _offsetY += pos.Y - _panLast.Y;
                _panLast = pos;
                ApplyCamera();
                break;

            case DragMode.Move when _activeItem != null:
                {
                    double dx = (pos.X - _pressScreen.X) / _zoom;
                    double dy = (pos.Y - _pressScreen.Y) / _zoom;

                    // С Shift к сетке привязывается та картинка, которую держим, остальные едут вместе с ней
                    if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                    {
                        dx = Snap(_startX + dx) - _startX;
                        dy = Snap(_startY + dy) - _startY;
                    }

                    foreach (var (item, x, y) in _groupStart)
                    {
                        item.X = x + dx;
                        item.Y = y + dy;
                    }

                    Board.NotifyItemChanged();
                    break;
                }

            case DragMode.Resize when _activeItem != null:
                {
                    double dx = (pos.X - _pressScreen.X) / _zoom;
                    double dy = (pos.Y - _pressScreen.Y) / _zoom;

                    // Пропорции сохраняем: берём тот масштаб, который изменился сильнее
                    double scaleX = (_startW + dx) / _startW;
                    double scaleY = (_startH + dy) / _startH;
                    double s = Math.Abs(scaleX - 1) > Math.Abs(scaleY - 1) ? scaleX : scaleY;

                    double newW = Math.Max(MinItemWidth, _startW * s);
                    if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                        newW = Math.Max(GridBackground.CellSize, Snap(newW));

                    _activeItem.Width = newW;
                    _activeItem.Height = newW * _startH / _startW;
                    Board.NotifyItemChanged();
                    break;
                }

            case DragMode.Marquee:
                {
                    var rect = new Rect(
                        Math.Min(_pressScreen.X, pos.X), Math.Min(_pressScreen.Y, pos.Y),
                        Math.Abs(pos.X - _pressScreen.X), Math.Abs(pos.Y - _pressScreen.Y));

                    var selection = new HashSet<ImageItem>(_marqueeBase);
                    foreach (var item in Board.ItemsIntersecting(rect))
                        selection.Add(item);

                    Board.SetSelection(selection);
                    Board.MarqueeRect = rect;
                    break;
                }

            case DragMode.None:
                {
                    // Просто водим мышью: подсказываем курсором, что можно сделать
                    var hit = Board.HitTest(pos, out bool isHandle);
                    bool canResize = isHandle && (hit == null || !Board.IsSelected(hit) || Board.Selection.Count == 1);
                    SetCursor(hit == null ? _arrowCursor : (canResize ? _resizeCursor : _handCursor));
                    break;
                }
        }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_mode != DragMode.None)
            e.Pointer.Capture(null); // дальше сработает PointerCaptureLost
    }

    private void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_mode == DragMode.Marquee)
            Board.MarqueeRect = null;

        _mode = DragMode.None;
        _activeItem = null;
        _groupStart.Clear();
        _marqueeBase.Clear();
        SetCursor(_arrowCursor);
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        // Ctrl (или Cmd на macOS) + колесо = зум
        if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0)
        {
            double factor = e.Delta.Y > 0 ? 1.1 : 1.0 / 1.1;
            ZoomAt(e.GetPosition(Viewport), factor);
        }
        else
        {
            _offsetX += e.Delta.X * WheelStep;
            _offsetY += e.Delta.Y * WheelStep;
            ApplyCamera();
        }

        e.Handled = true;
    }

    // =====================================================================
    //  Клавиатура
    // =====================================================================

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // Пока открыт просмотр, клавиши работают только в нём
        if (ViewerScreen.IsVisible)
        {
            HandleViewerKey(e);
            return;
        }

        if (StartScreen.IsVisible) return;

        bool ctrl = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;

        if (ctrl && (e.Key == Key.D0 || e.Key == Key.NumPad0))
        {
            ResetCamera();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.A)
        {
            Board.SelectAll();
            e.Handled = true;
        }
        else if (!ctrl && e.Key == Key.Escape)
        {
            Board.ClearSelection();
            e.Handled = true;
        }
        else if (!ctrl && e.Key == Key.F)
        {
            FitToContent();
            e.Handled = true;
        }
        else if (!ctrl && e.Key == Key.Enter)
        {
            var item = Board.PrimaryItem;
            if (item != null)
            {
                OpenViewer(item);
                e.Handled = true;
            }
        }
    }

    // =====================================================================
    //  Полноэкранный просмотр
    // =====================================================================

    private void OpenViewer(ImageItem item)
    {
        // Листаем по имени файла, а не по расположению на холсте
        _viewerItems.Clear();
        _viewerItems.AddRange(Board.Items.OrderBy(i => i.FileName, StringComparer.OrdinalIgnoreCase));

        int index = _viewerItems.IndexOf(item);
        if (index < 0) return;

        ViewerScreen.IsVisible = true;
        ShowViewerImage(index);
    }

    private void CloseViewer()
    {
        _viewerCts?.Cancel();
        ViewerScreen.IsVisible = false;
        ViewerImage.Source = null;

        var old = _viewerBitmap;
        _viewerBitmap = null;
        old?.Dispose();

        _viewerItems.Clear();
    }

    private void StepViewer(int delta)
    {
        if (_viewerItems.Count == 0) return;

        int count = _viewerItems.Count;
        int index = ((_viewerIndex + delta) % count + count) % count;
        ShowViewerImage(index);
    }

    private async void ShowViewerImage(int index)
    {
        if (_viewerItems.Count == 0) return;

        _viewerIndex = index;
        var item = _viewerItems[index];

        _viewerCts?.Cancel();
        var cts = new CancellationTokenSource();
        _viewerCts = cts;
        var ct = cts.Token;

        ViewerCaption.Text = $"{item.FileName}    {index + 1} / {_viewerItems.Count}";

        // Сразу показываем лучшее, что уже есть в памяти (оно может быть мыльным),
        // а чёткую копию под размер окна подгружаем следом
        var previous = _viewerBitmap;
        _viewerBitmap = null;
        ViewerImage.Source = item.GetBestBitmap();
        previous?.Dispose();

        double availW = Math.Max(64, ViewerScreen.Bounds.Width - 128) * RenderScaling;
        double availH = Math.Max(64, ViewerScreen.Bounds.Height - 112) * RenderScaling;
        double aspect = item.Height > 0 ? item.Width / item.Height : 1.0;
        int width = (int)Math.Clamp(Math.Min(availW, availH * aspect), 64, 4096);

        try
        {
            var bitmap = await Task.Run(() => ImageLoader.TryDecode(item.FilePath, width), ct);
            if (bitmap == null) return;

            if (ct.IsCancellationRequested || !ViewerScreen.IsVisible)
            {
                bitmap.Dispose();
                return;
            }

            var old = _viewerBitmap;
            _viewerBitmap = bitmap;
            ViewerImage.Source = bitmap;
            old?.Dispose();
        }
        catch (OperationCanceledException)
        {
            // перешли к другой картинке или закрыли просмотр
        }
        catch
        {
            // не получилось открыть файл: остаётся мыльная копия
        }
    }

    private void HandleViewerKey(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                CloseViewer();
                e.Handled = true;
                break;

            case Key.Left:
                StepViewer(-1);
                e.Handled = true;
                break;

            case Key.Right:
                StepViewer(1);
                e.Handled = true;
                break;
        }
    }

    private void OnViewerPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Клик по самой картинке ничего не делает, клик по тёмному фону закрывает.
        // Кнопки (✕ ‹ ›) обрабатывают нажатие сами, сюда оно не доходит.
        if (e.Source is Image) return;
        CloseViewer();
        e.Handled = true;
    }

    private void OnViewerCloseClick(object? sender, RoutedEventArgs e) => CloseViewer();
    private void OnViewerPrevClick(object? sender, RoutedEventArgs e) => StepViewer(-1);
    private void OnViewerNextClick(object? sender, RoutedEventArgs e) => StepViewer(1);
}