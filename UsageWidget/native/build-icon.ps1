param([string]$PreviewPath)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $PSScriptRoot)) { throw 'Icon output directory is missing' }
if ($PreviewPath -and -not (Test-Path -LiteralPath (Split-Path $PreviewPath))) { throw 'Preview directory is missing' }
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

public static class UsageIconBuilder {
  static Bitmap Draw(int size) {
    using (var large = new Bitmap(size * 4, size * 4, PixelFormat.Format32bppArgb)) {
      using (var graphics = Graphics.FromImage(large)) {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.ScaleTransform(size * 4 / 64f, size * 4 / 64f);
        using (var background = new SolidBrush(Color.FromArgb(20, 24, 33))) graphics.FillEllipse(background, 1, 1, 62, 62);
        using (var track = new Pen(Color.FromArgb(57, 67, 83), 6)) graphics.DrawEllipse(track, 13, 13, 38, 38);
        using (var gradient = new LinearGradientBrush(new Rectangle(0, 0, 64, 64),
          Color.FromArgb(102, 232, 235), Color.FromArgb(84, 118, 255), LinearGradientMode.ForwardDiagonal))
        using (var arc = new Pen(gradient, 6)) {
          arc.StartCap = LineCap.Round; arc.EndCap = LineCap.Round;
          graphics.DrawArc(arc, 13, 13, 38, 38, -90, 270);
        }
        using (var dot = new SolidBrush(Color.FromArgb(239, 246, 255))) graphics.FillEllipse(dot, 28, 28, 8, 8);
      }
      var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
      using (var graphics = Graphics.FromImage(bitmap)) {
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.DrawImage(large, new Rectangle(0, 0, size, size));
      }
      return bitmap;
    }
  }
  public static void Build(string path, string preview) {
    int[] sizes = { 16, 20, 24, 32, 48, 64, 128, 256 };
    var frames = new List<byte[]>();
    foreach (int size in sizes) using (var bitmap = Draw(size)) using (var stream = new MemoryStream()) {
      bitmap.Save(stream, ImageFormat.Png); frames.Add(stream.ToArray());
      if (size == 256 && !string.IsNullOrEmpty(preview)) bitmap.Save(preview, ImageFormat.Png);
    }
    using (var writer = new BinaryWriter(File.Create(path))) {
      writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length);
      uint offset = (uint)(6 + 16 * sizes.Length);
      for (int i = 0; i < sizes.Length; i++) {
        writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i])); writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
        writer.Write((byte)0); writer.Write((byte)0); writer.Write((ushort)1); writer.Write((ushort)32);
        writer.Write((uint)frames[i].Length); writer.Write(offset); offset += (uint)frames[i].Length;
      }
      foreach (var frame in frames) writer.Write(frame);
    }
  }
}
'@
[UsageIconBuilder]::Build((Join-Path $PSScriptRoot 'UsageWidget.ico'), $PreviewPath)
