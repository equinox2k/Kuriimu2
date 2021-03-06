﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Cyotek.Windows.Forms;
using Kontract;
using Kontract.Interfaces.Archive;
using Kontract.Interfaces.Common;
using Kontract.Interfaces.Image;
using Kontract.Models;
using Kontract.Models.Image;
using Kore;
using Kore.Files.Models;
using Kore.Utilities;
using Kore.Utilities.Palettes;
using Kuriimu2_WinForms.Interfaces;
using Kuriimu2_WinForms.Properties;
using Convert = System.Convert;
using Image = System.Drawing.Image;

namespace Kuriimu2_WinForms.FormatForms
{
    public partial class ImageForm : UserControl, IKuriimuForm
    {
        private IArchiveAdapter _parentAdapter;
        private TabPage _currentTab;
        private TabPage _parentTab;

        private IImageAdapter _imageAdapter => (IImageAdapter)Kfi.Adapter;
        private int _selectedImageIndex = 0;
        private BitmapInfo _selectedBitmapInfo => _imageAdapter.BitmapInfos[_selectedImageIndex];
        private Bitmap[] _bestBitmaps;
        private Bitmap _thumbnailBackground;

        Dictionary<string, string> _stylesText = new Dictionary<string, string>
        {
            ["None"] = "None",
            ["FixedSingle"] = "Simple",
            ["FixedSingleDropShadow"] = "Drop Shadow",
            ["FixedSingleGlowShadow"] = "Glow Shadow"
        };

        Dictionary<string, string> _stylesImages = new Dictionary<string, string>
        {
            ["None"] = "menu_border_none",
            ["FixedSingle"] = "menu_border_simple",
            ["FixedSingleDropShadow"] = "menu_border_drop_shadow",
            ["FixedSingleGlowShadow"] = "menu_border_glow_shadow"
        };

        public ImageForm(KoreFileInfo kfi, TabPage tabPage, IArchiveAdapter parentAdapter, TabPage parentTabPage)
        {
            InitializeComponent();

            Kfi = kfi;
            _currentTab = tabPage;
            _parentTab = parentTabPage;
            _parentAdapter = parentAdapter;

            try
            {
                if (_imageAdapter.BitmapInfos == null)
                    throw new ArgumentNullException(nameof(_imageAdapter.BitmapInfos));
                if (_imageAdapter.ImageEncodingInfos == null)
                    throw new ArgumentNullException(nameof(_imageAdapter.ImageEncodingInfos));
                if (_imageAdapter is IIndexedImageAdapter indexAdapter)
                {
                    if (indexAdapter.PaletteEncodingInfos == null)
                        throw new ArgumentNullException(nameof(indexAdapter.PaletteEncodingInfos));
                }
            }
            catch
            {
                throw new InvalidOperationException($"The plugin missed to implement a property.");
            }

            _bestBitmaps = _imageAdapter.BitmapInfos.Select(x => (Bitmap)x.Image.Clone()).ToArray();

            imbPreview.Image = _imageAdapter.BitmapInfos.FirstOrDefault()?.Image;

            // Populate format dropdown
            tsbFormat.DropDownItems.AddRange(_imageAdapter.ImageEncodingInfos?.Select(f => new ToolStripMenuItem { Text = f.EncodingName, Tag = f, Checked = f.EncodingIndex == _selectedBitmapInfo.ImageEncoding.EncodingIndex }).ToArray());
            if (tsbFormat.DropDownItems.Count > 0)
                foreach (var tsb in tsbFormat.DropDownItems)
                    ((ToolStripMenuItem)tsb).Click += tsbFormat_Click;

            // populate palette format dropdown
            if (_imageAdapter is IIndexedImageAdapter indexAdapter2 && _selectedBitmapInfo is IndexedBitmapInfo indexInfo)
            {
                tsbPalette.DropDownItems.AddRange(indexAdapter2.PaletteEncodingInfos?.Select(f => new ToolStripMenuItem { Text = f.EncodingName, Tag = f, Checked = f.EncodingIndex == indexInfo.PaletteEncoding.EncodingIndex }).ToArray());
                if (tsbPalette.DropDownItems.Count > 0)
                    foreach (var tsb in tsbPalette.DropDownItems)
                        ((ToolStripMenuItem)tsb).Click += tsbPalette_Click;
            }

            tsbImageBorderStyle.DropDownItems.AddRange(Enum.GetNames(typeof(ImageBoxBorderStyle)).Select(s => new ToolStripMenuItem { Image = (Image)Resources.ResourceManager.GetObject(_stylesImages[s]), Text = _stylesText[s], Tag = s }).ToArray());
            foreach (var tsb in tsbImageBorderStyle.DropDownItems)
                ((ToolStripMenuItem)tsb).Click += tsbImageBorderStyle_Click;

            UpdateForm();
            UpdatePreview();
            UpdateImageList();
        }

        private async void tsbFormat_Click(object sender, EventArgs e)
        {
            var tsb = (ToolStripMenuItem)sender;

            if (_selectedBitmapInfo.ImageEncoding.EncodingIndex != ((EncodingInfo)tsb.Tag).EncodingIndex)
            {
                _selectedBitmapInfo.Image = (Bitmap)_bestBitmaps[_selectedImageIndex].Clone();
                var indexInfo = _selectedBitmapInfo as IndexedBitmapInfo;
                var indexAdapter = _imageAdapter as IIndexedImageAdapter;
                var result = await ImageEncode(_selectedBitmapInfo, (EncodingInfo)tsb.Tag, indexInfo?.PaletteEncoding ?? indexAdapter?.PaletteEncodingInfos.FirstOrDefault());

                if (result)
                {
                    foreach (ToolStripMenuItem tsm in tsbFormat.DropDownItems)
                        tsm.Checked = false;
                    tsb.Checked = true;
                }
            }
        }

        private async void tsbPalette_Click(object sender, EventArgs e)
        {
            var tsb = (ToolStripMenuItem)sender;

            if (_selectedBitmapInfo is IndexedBitmapInfo indexInfo)
                if (indexInfo.PaletteEncoding.EncodingIndex != ((EncodingInfo)tsb.Tag).EncodingIndex)
                {
                    indexInfo.Image = (Bitmap)_bestBitmaps[_selectedImageIndex].Clone();
                    var result = await ImageEncode(indexInfo, indexInfo.ImageEncoding, (EncodingInfo)tsb.Tag);

                    if (result)
                    {
                        foreach (ToolStripMenuItem tsm in tsbPalette.DropDownItems)
                            tsm.Checked = false;
                        tsb.Checked = true;
                    }
                }
        }

        public KoreFileInfo Kfi { get; set; }
        public Color TabColor { get; set; }

        public event EventHandler<SaveTabEventArgs> SaveTab;
        public event EventHandler<CloseTabEventArgs> CloseTab;
        public event EventHandler<ProgressReport> ReportProgress;

        public void Close()
        {
            CloseTab?.Invoke(this, new CloseTabEventArgs(Kfi) { LeaveOpen = Kfi.ParentKfi != null });
        }

        private void SaveAs()
        {
            var sfd = new SaveFileDialog();
            sfd.FileName = Path.GetFileName(Kfi.StreamFileInfo.FileName);
            sfd.Filter = "All Files (*.*)|*.*";

            if (sfd.ShowDialog() == DialogResult.OK)
                Save(sfd.FileName);
            else
                MessageBox.Show("No save location was chosen", "Save Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        public void Save(string filename = "")
        {
            SaveTab?.Invoke(this, new SaveTabEventArgs(Kfi) { NewSaveFile = filename });

            UpdateParent();
            UpdateForm();
        }

        private void ExportPng()
        {
            SaveFileDialog sfd = new SaveFileDialog
            {
                Title = "Export PNG...",
                InitialDirectory = Settings.Default.LastDirectory,
                FileName = Path.GetFileNameWithoutExtension(Kfi.StreamFileInfo.FileName) + "." + _selectedImageIndex.ToString("00") + ".png",
                Filter = "Portable Network Graphics (*.png)|*.png",
                AddExtension = true
            };

            if (sfd.ShowDialog() == DialogResult.OK)
                _selectedBitmapInfo.Image.Save(sfd.FileName, ImageFormat.Png);
        }

        private void ImportPng()
        {
            OpenFileDialog ofd = new OpenFileDialog
            {
                Title = "Import PNG...",
                InitialDirectory = Settings.Default.LastDirectory,
                Filter = "Portable Network Graphics (*.png)|*.png"
            };

            if (ofd.ShowDialog() == DialogResult.OK)
                Import(ofd.FileName);
        }

        private async void Import(string filename)
        {
            try
            {
                _selectedBitmapInfo.Image = new Bitmap(filename);
                _bestBitmaps[_selectedImageIndex] = (Bitmap)_selectedBitmapInfo.Image.Clone();
                var indexInfo = _selectedBitmapInfo as IndexedBitmapInfo;
                await ImageEncode(_selectedBitmapInfo, _selectedBitmapInfo.ImageEncoding, indexInfo?.PaletteEncoding);

                treBitmaps.SelectedNode = treBitmaps.Nodes[_selectedImageIndex];
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString(), ex.Message, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async Task<bool> ImageEncode(BitmapInfo bitmapInfo, EncodingInfo imageEncoding, EncodingInfo paletteEncoding)
        {
            if (!tsbFormat.Enabled && !tsbPalette.Enabled)
                return false;
            if (_imageAdapter is IIndexedImageAdapter && imageEncoding.IsIndexed && paletteEncoding == null)
                return false;

            var report = new Progress<ProgressReport>();
            report.ProgressChanged += Report_ProgressChanged;

            DisablePaletteControls();
            DisableImageControls();

            bool commitResult;
            try
            {
                ImageTranscodeResult result;
                if (_imageAdapter is IIndexedImageAdapter indexAdapter && imageEncoding.IsIndexed)
                {
                    result = await indexAdapter.TranscodeImage(bitmapInfo, imageEncoding, paletteEncoding, report);
                    if (!result.Result)
                    {
                        MessageBox.Show(result.Exception?.ToString() ?? "Encoding was not successful.",
                            "Encoding was not successful", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        UpdateForm();
                        return result.Result;
                    }

                    commitResult = indexAdapter.Commit(bitmapInfo, result.Image, imageEncoding, result.Palette, paletteEncoding);
                }
                else
                {
                    result = await _imageAdapter.TranscodeImage(bitmapInfo, imageEncoding, report);
                    if (!result.Result)
                    {
                        MessageBox.Show(result.Exception?.ToString() ?? "Encoding was not successful.",
                            "Encoding was not successful", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        UpdateForm();
                        return result.Result;
                    }

                    commitResult = _imageAdapter.Commit(bitmapInfo, result.Image, imageEncoding);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString(), "Exception Caught", MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateForm();
                return false;
            }

            UpdateForm();
            UpdatePreview();
            UpdateImageList();

            return commitResult;
        }

        private void Report_ProgressChanged(object sender, ProgressReport e)
        {
            ReportProgress?.Invoke(this, e);
            //pbEncoding.Text = $"{(e.HasMessage ? $"{e.Message} - " : string.Empty)}{e.Percentage}%";
            //pbEncoding.Value = Convert.ToInt32(e.Percentage);
        }

        public void UpdateParent()
        {
            if (_parentTab != null)
                if (_parentTab.Controls[0] is IArchiveForm archiveForm)
                {
                    archiveForm.UpdateForm();
                    archiveForm.UpdateParent();
                }
        }

        public void UpdateForm()
        {
            _currentTab.Text = Kfi.DisplayName;

            tsbSave.Enabled = _imageAdapter is ISaveFiles;
            tsbSaveAs.Enabled = _imageAdapter is ISaveFiles && Kfi.ParentKfi == null;

            var isIndexed = _selectedBitmapInfo is IndexedBitmapInfo;
            tslPalette.Visible = isIndexed;
            tsbPalette.Visible = isIndexed;
            tsbPalette.Enabled = isIndexed && ((_imageAdapter as IIndexedImageAdapter).PaletteEncodingInfos?.Any() ?? false);
            pbPalette.Enabled = isIndexed;
            tsbPaletteImport.Enabled = isIndexed;

            splProperties.Panel2Collapsed = !isIndexed;

            tsbFormat.Enabled = _imageAdapter.ImageEncodingInfos?.Any() ?? false;
        }

        private void ImageForm_Load(object sender, EventArgs e)
        {
            Dock = DockStyle.Fill;
            Padding = new Padding(3);
        }

        #region Events
        private void tsbGridColor1_Click(object sender, EventArgs e)
        {
            SetGridColor(imbPreview.GridColor, (clr) =>
            {
                imbPreview.GridColor = clr;
                Settings.Default.GridColor1 = clr;
                Settings.Default.Save();
            });
        }

        private void tsbGridColor2_Click(object sender, EventArgs e)
        {
            SetGridColor(imbPreview.GridColorAlternate, (clr) =>
            {
                imbPreview.GridColorAlternate = clr;
                Settings.Default.GridColor2 = clr;
                Settings.Default.Save();
            });
        }

        private void tsbImageBorderStyle_Click(object sender, EventArgs e)
        {
            var tsb = (ToolStripMenuItem)sender;
            var style = (ImageBoxBorderStyle)Enum.Parse(typeof(ImageBoxBorderStyle), tsb.Tag.ToString());

            imbPreview.ImageBorderStyle = style;
            Settings.Default.ImageBorderStyle = style;
            Settings.Default.Save();
            UpdatePreview();
        }

        private void tsbImageBorderColor_Click(object sender, EventArgs e)
        {
            clrDialog.Color = imbPreview.ImageBorderColor;
            if (clrDialog.ShowDialog() != DialogResult.OK) return;

            imbPreview.ImageBorderColor = clrDialog.Color;
            Settings.Default.ImageBorderColor = clrDialog.Color;
            Settings.Default.Save();
            UpdatePreview();
        }
        #endregion

        #region Private methods
        private void SetGridColor(Color startColor, Action<Color> setColorToProperties)
        {
            clrDialog.Color = imbPreview.GridColor;
            if (clrDialog.ShowDialog() != DialogResult.OK) return;

            setColorToProperties(clrDialog.Color);

            UpdatePreview();
            GenerateThumbnailBackground();
            UpdateImageList();

            treBitmaps.SelectedNode = treBitmaps.Nodes[_selectedImageIndex];
        }

        private void UpdatePreview()
        {
            if (_imageAdapter.BitmapInfos.Count > 0)
            {
                imbPreview.Image = _selectedBitmapInfo.Image;
                imbPreview.Zoom -= 1;
                imbPreview.Zoom += 1;
                //pptImageProperties.SelectedObject = _selectedBitmapInfo;
            }

            // Grid Color 1
            imbPreview.GridColor = Settings.Default.GridColor1;
            var gc1Bitmap = new Bitmap(16, 16, PixelFormat.Format24bppRgb);
            var gfx = Graphics.FromImage(gc1Bitmap);
            gfx.FillRectangle(new SolidBrush(Settings.Default.GridColor1), 0, 0, 16, 16);
            tsbGridColor1.Image = gc1Bitmap;

            // Grid Color 2
            imbPreview.GridColorAlternate = Settings.Default.GridColor2;
            var gc2Bitmap = new Bitmap(16, 16, PixelFormat.Format24bppRgb);
            gfx = Graphics.FromImage(gc2Bitmap);
            gfx.FillRectangle(new SolidBrush(Settings.Default.GridColor2), 0, 0, 16, 16);
            tsbGridColor2.Image = gc2Bitmap;

            // Image Border Style
            imbPreview.ImageBorderStyle = Settings.Default.ImageBorderStyle;
            tsbImageBorderStyle.Image = (Image)Resources.ResourceManager.GetObject(_stylesImages[Settings.Default.ImageBorderStyle.ToString()]);
            tsbImageBorderStyle.Text = _stylesText[Settings.Default.ImageBorderStyle.ToString()];

            // Image Border Color
            imbPreview.ImageBorderColor = Settings.Default.ImageBorderColor;
            var ibcBitmap = new Bitmap(16, 16, PixelFormat.Format24bppRgb);
            gfx = Graphics.FromImage(ibcBitmap);
            gfx.FillRectangle(new SolidBrush(Settings.Default.ImageBorderColor), 0, 0, 16, 16);
            tsbImageBorderColor.Image = ibcBitmap;

            // Format Dropdown
            tsbFormat.Text = _selectedBitmapInfo.ImageEncoding.EncodingName;
            tsbFormat.Tag = _selectedBitmapInfo.ImageEncoding.EncodingIndex;
            // Update selected format
            foreach (ToolStripMenuItem tsm in tsbFormat.DropDownItems)
                tsm.Checked = ((EncodingInfo)tsm.Tag).EncodingIndex == _selectedBitmapInfo.ImageEncoding.EncodingIndex;

            if (_imageAdapter is IIndexedImageAdapter && _selectedBitmapInfo is IndexedBitmapInfo indexedInfo)
            {
                // Palette Dropdown
                tsbPalette.Text = indexedInfo.PaletteEncoding.EncodingName;
                tsbPalette.Tag = indexedInfo.PaletteEncoding.EncodingIndex;
                // Update selected palette format
                foreach (ToolStripMenuItem tsm in tsbPalette.DropDownItems)
                    tsm.Checked = ((EncodingInfo)tsm.Tag).EncodingIndex == indexedInfo.PaletteEncoding.EncodingIndex;

                // Palette Picture Box
                var dimPalette = Convert.ToInt32(Math.Sqrt(indexedInfo.ColorCount));
                var paletteImg = ComposeImage(indexedInfo.Palette, dimPalette, dimPalette);
                if (paletteImg != null)
                    pbPalette.Image = paletteImg;
            }

            // Dimensions
            tslWidth.Text = _selectedBitmapInfo.Size.Width.ToString();
            tslHeight.Text = _selectedBitmapInfo.Size.Height.ToString();
        }

        public static Bitmap ComposeImage(IList<Color> colors, int width, int height)
        {
            var image = new Bitmap(width, height);
            BitmapData data;
            try
            {
                data = image.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            }
            catch (Exception ex)
            {
                Trace.WriteLine(ex.ToString());
                return null;
            }
            unsafe
            {
                var ptr = (int*)data.Scan0;
                for (int i = 0; i < image.Width * image.Height; i++)
                {
                    if (i >= colors.Count)
                        break;
                    ptr[i] = colors[i].ToArgb();
                }
            }
            image.UnlockBits(data);

            return image;
        }

        private void GenerateThumbnailBackground()
        {
            var thumbWidth = Settings.Default.ThumbnailWidth;
            var thumbHeight = Settings.Default.ThumbnailHeight;
            var thumb = new Bitmap(thumbWidth, thumbHeight, PixelFormat.Format24bppRgb);
            var gfx = Graphics.FromImage(thumb);

            // Grid
            var xCount = Settings.Default.ThumbnailWidth / 16 + 1;
            var yCount = Settings.Default.ThumbnailHeight / 16 + 1;

            gfx.FillRectangle(new SolidBrush(Settings.Default.GridColor1), 0, 0, thumbWidth, thumbHeight);
            for (var i = 0; i < xCount; i++)
                for (var j = 0; j < yCount; j++)
                    if ((i + j) % 2 != 1)
                        gfx.FillRectangle(new SolidBrush(Settings.Default.GridColor2), i * 16, j * 16, 16, 16);

            _thumbnailBackground = thumb;
        }

        private void UpdateImageList()
        {
            if (_imageAdapter.BitmapInfos.Count <= 0) return;

            treBitmaps.BeginUpdate();
            treBitmaps.Nodes.Clear();
            imlBitmaps.Images.Clear();
            imlBitmaps.TransparentColor = Color.Transparent;
            imlBitmaps.ImageSize = new Size(Settings.Default.ThumbnailWidth, Settings.Default.ThumbnailHeight);
            treBitmaps.ItemHeight = Settings.Default.ThumbnailHeight + 6;

            for (var i = 0; i < _imageAdapter.BitmapInfos.Count; i++)
            {
                var bitmapInfo = _imageAdapter.BitmapInfos[i];
                if (bitmapInfo.Image == null) continue;
                imlBitmaps.Images.Add(i.ToString(), GenerateThumbnail(bitmapInfo.Image));
                treBitmaps.Nodes.Add(new TreeNode
                {
                    Text = !string.IsNullOrEmpty(bitmapInfo.Name) ? bitmapInfo.Name : i.ToString("00"),
                    Tag = i,
                    ImageKey = i.ToString(),
                    SelectedImageKey = i.ToString()
                });
            }

            treBitmaps.EndUpdate();
        }

        private Bitmap GenerateThumbnail(Bitmap input)
        {
            var thumbWidth = Settings.Default.ThumbnailWidth;
            var thumbHeight = Settings.Default.ThumbnailHeight;
            var thumb = new Bitmap(thumbWidth, thumbHeight, PixelFormat.Format24bppRgb);
            var gfx = Graphics.FromImage(thumb);

            gfx.CompositingQuality = CompositingQuality.HighSpeed;
            gfx.PixelOffsetMode = PixelOffsetMode.Default;
            gfx.SmoothingMode = SmoothingMode.HighSpeed;
            gfx.InterpolationMode = InterpolationMode.Default;

            var wRatio = (float)input.Width / thumbWidth;
            var hRatio = (float)input.Height / thumbHeight;
            var ratio = wRatio >= hRatio ? wRatio : hRatio;

            if (input.Width <= thumbWidth && input.Height <= thumbHeight)
                ratio = 1.0f;

            var size = new Size((int)Math.Min(input.Width / ratio, thumbWidth), (int)Math.Min(input.Height / ratio, thumbHeight));
            var pos = new Point(thumbWidth / 2 - size.Width / 2, thumbHeight / 2 - size.Height / 2);

            // Grid
            if (_thumbnailBackground == null)
                GenerateThumbnailBackground();

            gfx.DrawImageUnscaled(_thumbnailBackground, 0, 0, _thumbnailBackground.Width, _thumbnailBackground.Height);
            gfx.InterpolationMode = ratio != 1.0f ? InterpolationMode.HighQualityBicubic : InterpolationMode.Default;
            gfx.DrawImage(input, pos.X, pos.Y, size.Width, size.Height);

            return thumb;
        }
        #endregion

        private void imbPreview_Zoomed(object sender, ImageBoxZoomEventArgs e)
        {
            tslZoom.Text = "Zoom: " + imbPreview.Zoom + "%";
        }

        private void imbPreview_MouseEnter(object sender, EventArgs e)
        {
            imbPreview.Focus();
        }

        private bool _setIndexInImage;
        private void imbPreview_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space)
            {
                imbPreview.SelectionMode = ImageBoxSelectionMode.None;
                imbPreview.Cursor = Cursors.SizeAll;
                tslTool.Text = "Tool: Pan";
            }

            if (e.KeyCode == Keys.ShiftKey)
            {
                _setIndexInImage = true;
            }
        }

        private void imbPreview_KeyUp(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space)
            {
                imbPreview.SelectionMode = ImageBoxSelectionMode.Zoom;
                imbPreview.Cursor = Cursors.Default;
                tslTool.Text = "Tool: Zoom";
            }

            if (e.KeyCode == Keys.ShiftKey)
            {
                _setIndexInImage = false;
            }
        }

        private void treBitmaps_MouseEnter(object sender, EventArgs e)
        {
            treBitmaps.Focus();
        }

        private void treBitmaps_AfterSelect(object sender, TreeViewEventArgs e)
        {
            _selectedImageIndex = treBitmaps.SelectedNode.Index;
            UpdatePreview();
        }

        private void tsbSave_Click(object sender, EventArgs e)
        {
            Save();
        }

        private void tsbSaveAs_Click(object sender, EventArgs e)
        {
            SaveAs();
        }

        private void tsbExport_Click(object sender, EventArgs e)
        {
            ExportPng();
        }

        private void tsbImport_Click(object sender, EventArgs e)
        {
            ImportPng();
        }

        private void imbPreview_DragEnter(object sender, DragEventArgs e)
        {
            e.Effect = DragDropEffects.Copy;
        }

        private void imbPreview_DragDrop(object sender, DragEventArgs e)
        {
            string[] files = (string[])e.Data.GetData(DataFormats.FileDrop, false);
            if (files.Length > 0 && File.Exists(files[0]))
                Import(files[0]);
        }

        private int _paletteChosenColorIndex = -1;
        private void PbPalette_MouseClick(object sender, MouseEventArgs e)
        {
            if (_paletteChooseColor)
                _paletteChosenColorIndex = GetPaletteIndex(e.Location);
            else
                SetColorInPalette(GetPaletteIndex, e.Location);
        }

        private void ImbPreview_MouseClick(object sender, MouseEventArgs e)
        {
            if (_setIndexInImage && _paletteChosenColorIndex >= 0)
                SetIndexInImage(e.Location, _paletteChosenColorIndex);
            else
                SetColorInPalette(GetPaletteIndexByImageLocation, e.Location);
        }

        private async void SetColorInPalette(Func<Point, int> indexFunc, Point controlPoint)
        {
            if (!(_imageAdapter is IIndexedImageAdapter indexAdapter) || !(_selectedBitmapInfo is IndexedBitmapInfo indexInfo))
                return;

            DisablePaletteControls();
            DisableImageControls();

            var index = indexFunc(controlPoint);
            if (index < 0 || index >= indexInfo.ColorCount)
            {
                UpdateForm();
                return;
            }

            if (clrDialog.ShowDialog() != DialogResult.OK)
            {
                UpdateForm();
                return;
            }

            var progress = new Progress<ProgressReport>();
            progress.ProgressChanged += Report_ProgressChanged;
            bool commitRes;
            try
            {
                var result = await indexAdapter.SetColorInPalette(indexInfo, index, clrDialog.Color, progress);
                if (!result.Result)
                {
                    MessageBox.Show("Setting color in palette was not successful.", "Set color unsuccessful",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    UpdateForm();
                    return;
                }

                commitRes = indexAdapter.Commit(indexInfo, result.Image, indexInfo.ImageEncoding,
                    result.Palette, indexInfo.PaletteEncoding);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString(), "Exception catched", MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateForm();
                return;
            }

            if (!commitRes)
            {
                MessageBox.Show("Setting color in palette was not successful.", "Set color unsuccessful",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateForm();
                return;
            }

            // TODO: Currently reset the best bitmap to quantized image, so Encode will encode the quantized image with changed palette
            _bestBitmaps[_selectedImageIndex] = indexInfo.Image;

            UpdateForm();
            UpdatePreview();
            UpdateImageList();
        }

        private async void SetIndexInImage(Point controlPoint, int newIndex)
        {
            if (!(_imageAdapter is IIndexedImageAdapter indexAdapter) || !(_selectedBitmapInfo is IndexedBitmapInfo indexInfo))
                return;
            if (newIndex >= indexInfo.ColorCount)
                return;

            DisablePaletteControls();
            DisableImageControls();

            var pointInImg = GetPointInImage(controlPoint);
            if (pointInImg == Point.Empty)
            {
                UpdateForm();
                return;
            }

            var progress = new Progress<ProgressReport>();
            progress.ProgressChanged += Report_ProgressChanged;
            bool commitRes;
            try
            {
                var result = await indexAdapter.SetIndexInImage(indexInfo, pointInImg, newIndex, progress);
                if (!result.Result)
                {
                    MessageBox.Show("Setting index in image was not successful.", "Set index unsuccessful",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    UpdateForm();
                    return;
                }

                commitRes = indexAdapter.Commit(indexInfo, result.Image, indexInfo.ImageEncoding,
                    result.Palette, indexInfo.PaletteEncoding);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString(), "Exception catched", MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateForm();
                return;
            }

            if (!commitRes)
            {
                MessageBox.Show("Setting index in image was not successful.", "Set color unsuccessful",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateForm();
                return;
            }

            // TODO: Currently reset the best bitmap to quantized image, so Encode will encode the quantized image with changed palette
            _bestBitmaps[_selectedImageIndex] = indexInfo.Image;

            UpdateForm();
            UpdatePreview();
            UpdateImageList();
        }

        private void DisablePaletteControls()
        {
            pbPalette.Enabled = false;
            tsbPalette.Enabled = false;
        }

        private void DisableImageControls()
        {
            tsbFormat.Enabled = false;
        }

        private int GetPaletteIndex(Point point)
        {
            var xIndex = point.X / (pbPalette.Width / pbPalette.Image.Width);
            var yIndex = point.Y / (pbPalette.Height / pbPalette.Image.Height);
            return yIndex * pbPalette.Image.Width + xIndex;
        }

        private Point GetPointInImage(Point controlPoint)
        {
            if (!imbPreview.IsPointInImage(controlPoint))
                return Point.Empty;

            return imbPreview.PointToImage(controlPoint);
        }

        private int GetPaletteIndexByImageLocation(Point point)
        {
            var pointInImg = GetPointInImage(point);
            if (pointInImg == Point.Empty)
                return -1;
            var pixelColor = _selectedBitmapInfo.Image.GetPixel(pointInImg.X, pointInImg.Y);

            return (_selectedBitmapInfo as IndexedBitmapInfo)?.Palette.IndexOf(pixelColor) ?? -1;
        }

        private void TsbPaletteImport_Click(object sender, EventArgs e)
        {
            ImportPalette();
        }

        private async void ImportPalette()
        {
            if (!(_imageAdapter is IIndexedImageAdapter indexAdapter) || !(_selectedBitmapInfo is IndexedBitmapInfo indexInfo))
                return;

            var colors = LoadPaletteFile();
            if (colors == null)
                return;

            DisablePaletteControls();
            DisableImageControls();

            var progress = new Progress<ProgressReport>();
            progress.ProgressChanged += Report_ProgressChanged;
            bool commitRes;
            try
            {
                var result = await indexAdapter.SetPalette(indexInfo, colors, progress);
                if (!result.Result)
                {
                    MessageBox.Show("Setting color in palette was not successful.", "Set color unsuccessful",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    UpdateForm();
                    return;
                }

                commitRes = indexAdapter.Commit(indexInfo, result.Image, indexInfo.ImageEncoding,
                    colors, indexInfo.PaletteEncoding);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString(), "Exception catched", MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateForm();
                return;
            }

            if (!commitRes)
            {
                MessageBox.Show("Setting color in palette was not successful.", "Set color unsuccessful",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateForm();
                return;
            }

            // TODO: Currently reset the best bitmap to quantized image, so Encode will encode the quantized image with changed palette
            _bestBitmaps[_selectedImageIndex] = indexInfo.Image;

            UpdateForm();
            UpdatePreview();
            UpdateImageList();
        }

        private IList<Color> LoadPaletteFile()
        {
            OpenFileDialog ofd = new OpenFileDialog
            {
                Title = "Open palette...",
                InitialDirectory = Settings.Default.LastDirectory,
                Filter = "Kuriimu Palette (*.kpal)|*.kpal|Microsoft RIFF Palette (*.pal)|*.pal"
            };

            if (ofd.ShowDialog() != DialogResult.OK)
            {
                MessageBox.Show("Couldn't open palette file.", "Invalid file", MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return null;
            }

            IList<Color> palette = null;
            if (Path.GetExtension(ofd.FileName) == ".kpal")
            {
                try
                {
                    var kpal = KPal.FromFile(ofd.FileName);
                    palette = kpal.Palette;
                }
                catch (Exception e)
                {
                    MessageBox.Show(e.ToString(), "Exception catched.", MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
            }
            else if (Path.GetExtension(ofd.FileName) == ".pal")
            {
                try
                {
                    var pal = RiffPal.FromFile(ofd.FileName);
                    palette = pal.Palette;
                }
                catch (Exception e)
                {
                    MessageBox.Show(e.ToString(), "Exception catched.", MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
            }

            return palette;
        }

        private void TsbPaletteExport_Click(object sender, EventArgs e)
        {
            ExportPalette();
        }

        private void ExportPalette()
        {
            if (!(_imageAdapter is IIndexedImageAdapter indexAdapter) || !(_selectedBitmapInfo is IndexedBitmapInfo indexInfo))
                return;

            SavePaletteFile(indexInfo.Palette);
        }

        private void SavePaletteFile(IList<Color> colors)
        {
            SaveFileDialog sfd = new SaveFileDialog
            {
                Title = "Save palette...",
                InitialDirectory = Settings.Default.LastDirectory,
                Filter = "Kuriimu Palette (*.kpal)|*.kpal"
            };

            if (sfd.ShowDialog() != DialogResult.OK)
            {
                MessageBox.Show("Couldn't save palette file.", "Invalid file", MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }

            try
            {
                var kpal = new KPal(colors, 1, 8, 8, 8, 8);
                kpal.Save(sfd.FileName);
            }
            catch (Exception e)
            {
                MessageBox.Show(e.ToString(), "Exception catched.", MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private void PbPalette_MouseEnter(object sender, EventArgs e)
        {
            pbPalette.Focus();
        }

        private bool _paletteChooseColor;
        private void PbPalette_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.ShiftKey)
                _paletteChooseColor = true;
        }

        private void PbPalette_KeyUp(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.ShiftKey)
                _paletteChooseColor = false;
        }
    }
}
