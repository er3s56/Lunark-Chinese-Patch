using System.Linq;
using System.Reflection;
using System.Drawing;
using System.Drawing.Text;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.Collections;

EnsureDataLoaded();

string gameDir = Path.GetDirectoryName(FilePath);

// 优先使用游戏目录下的字体
string fontFile = Path.Combine(gameDir, "zpix.ttf");
if (!File.Exists(fontFile)) fontFile = Path.Combine(gameDir, "font.ttf");
if (!File.Exists(fontFile)) fontFile = Path.Combine(gameDir, "font.ttc");
if (!File.Exists(fontFile)) fontFile = @"C:\Windows\Fonts\msyh.ttc";

string langFile = Path.Combine(gameDir, "language.json");

if (!File.Exists(fontFile)) { ScriptError("找不到字体文件"); return; }
if (!File.Exists(langFile)) { ScriptError("找不到 language.json"); return; }

bool isPixelFont = fontFile.ToLower().Contains("zpix") || fontFile.ToLower().Contains("pixel");

// 中文字形高度占原始英文字形高度的比例（0.7 = 70%）
// 调小则字更小但不容易超屏，调大则字更大但可能溢出
float cnHeightRatio = 0.75f;

ScriptMessage("使用中文字体: " + fontFile + "\n像素字体: " + isPixelFont + "\n高度比例: " + cnHeightRatio);

// ---- 收集需要的中文字符 ----
HashSet<int> cjkSet = new HashSet<int>();
string text = File.ReadAllText(langFile, System.Text.Encoding.UTF8);
foreach (char c in text)
{
    int v = (int)c;
    if (v >= 0x00A0 && v <= 0x024F) cjkSet.Add(v);
    if (v >= 0x2000 && v <= 0x206F) cjkSet.Add(v);
    if (v >= 0x3000 && v <= 0x303F) cjkSet.Add(v);
    if (v >= 0x4E00 && v <= 0x9FFF) cjkSet.Add(v);
    if (v >= 0xFF00 && v <= 0xFFEF) cjkSet.Add(v);
}
List<int> cjkChars = cjkSet.OrderBy(x => x).ToList();

// ---- 加载中文字体 ----
PrivateFontCollection pfc = new PrivateFontCollection();
pfc.AddFontFile(fontFile);
if (pfc.Families.Length == 0) { ScriptError("无法加载字体"); return; }
FontFamily family = pfc.Families[0];

Func<int, int> Pow2 = (int n) =>
{
    n--; n |= n >> 1; n |= n >> 2; n |= n >> 4;
    n |= n >> 8; n |= n >> 16; n++;
    return Math.Max(n, 64);
};

Func<object, Bitmap> gmImageToBitmap = (object gmImage) =>
{
    if (gmImage == null) return null;
    var t = gmImage.GetType();
    var savePng = t.GetMethod("SavePng", new Type[] { typeof(Stream) });
    if (savePng == null) return null;
    using (var msO = new MemoryStream())
    {
        savePng.Invoke(gmImage, new object[] { msO });
        msO.Position = 0;
        return new Bitmap(msO);
    }
};

// ---- 查找字体所属的 TextureGroupInfo ----
Func<UndertaleEmbeddedTexture, object> findTextureGroup = (UndertaleEmbeddedTexture origTex) =>
{
    if (Data.TextureGroupInfo == null) return null;
    foreach (var tgi in Data.TextureGroupInfo)
    {
        try
        {
            var tpProp = tgi.GetType().GetProperty("TexturePages");
            if (tpProp == null) continue;
            var pages = tpProp.GetValue(tgi) as IList;
            if (pages == null) continue;
            foreach (var p in pages)
            {
                try
                {
                    var resProp = p.GetType().GetProperty("Resource");
                    var res = resProp?.GetValue(p);
                    if (res == origTex) return tgi;
                }
                catch { }
            }
        }
        catch { }
    }
    return null;
};

// ---- 向 TextureGroupInfo 添加新纹理 ----
Action<object, UndertaleEmbeddedTexture> addTextureToGroup = (object tgi, UndertaleEmbeddedTexture newTex) =>
{
    if (tgi == null) return;
    try
    {
        var tpProp = tgi.GetType().GetProperty("TexturePages");
        if (tpProp == null) return;
        var pages = tpProp.GetValue(tgi) as IList;
        if (pages == null) return;

        if (pages.Count == 0) return;
        Type itemType = pages[0].GetType();

        var ctor = itemType.GetConstructor(Type.EmptyTypes);
        if (ctor == null) return;
        var newItem = ctor.Invoke(null);

        var resProp = itemType.GetProperty("Resource");
        if (resProp != null && resProp.CanWrite)
            resProp.SetValue(newItem, newTex);

        pages.Add(newItem);
    }
    catch { }
};

// ---- 向 TextureGroupInfo 添加字体引用 ----
Action<object, UndertaleFont> addFontToGroup = (object tgi, UndertaleFont font) =>
{
    if (tgi == null) return;
    try
    {
        var fontsProp = tgi.GetType().GetProperty("Fonts");
        if (fontsProp == null) return;
        var fonts = fontsProp.GetValue(tgi) as IList;
        if (fonts == null || fonts.Count == 0) return;

        Type itemType = fonts[0].GetType();
        var ctor = itemType.GetConstructor(Type.EmptyTypes);
        if (ctor == null) return;
        var newItem = ctor.Invoke(null);

        var resProp = itemType.GetProperty("Resource");
        if (resProp != null && resProp.CanWrite)
            resProp.SetValue(newItem, font);

        fonts.Add(newItem);
    }
    catch { }
};

int patched = 0;
List<string> results = new List<string>();

foreach (var f in Data.Fonts)
{
    string fname = f.Name?.Content ?? "(unnamed)";
    float em = f.EmSize;
    if (em <= 0) em = 12;
    byte origAA = f.AntiAliasing;

    var origGlyphs = f.Glyphs.ToList();
    var origTPI = f.Texture;
    Bitmap origAtlasBmp = null;
    UndertaleEmbeddedTexture origEmbTex = null;

    if (origTPI != null && origTPI.TexturePage != null)
    {
        origEmbTex = origTPI.TexturePage;
        var imgProp = origTPI.TexturePage.TextureData.GetType().GetProperty("Image");
        if (imgProp != null)
        {
            var gmImg = imgProp.GetValue(origTPI.TexturePage.TextureData);
            origAtlasBmp = gmImageToBitmap(gmImg);
        }
    }

    if (origAtlasBmp == null)
    {
        results.Add("  SKIP " + fname + ": 无法读取原始纹理");
        continue;
    }

    int tpiOffX = origTPI.SourceX;
    int tpiOffY = origTPI.SourceY;

    // 找到原始纹理所属的 TextureGroupInfo
    object origTexGroup = findTextureGroup(origEmbTex);
    string groupName = "?";
    try { groupName = origTexGroup?.GetType().GetProperty("Name")?.GetValue(origTexGroup)?.ToString() ?? "null"; }
    catch { }

    int origGlyphH = 0;
    foreach (var og in origGlyphs)
        if (og.SourceHeight > origGlyphH) origGlyphH = og.SourceHeight;
    if (origGlyphH <= 0) origGlyphH = (int)(em * 1.5f);

    // 目标高度 = 原始高度 * 缩放比例
    int targetH = Math.Max((int)(origGlyphH * cnHeightRatio), 8);

    float renderEm = em;
    Font cnFont;
    try { cnFont = new Font(family, renderEm, FontStyle.Regular, GraphicsUnit.Pixel); }
    catch (Exception ex) { results.Add("  SKIP " + fname + ": " + ex.Message); continue; }

    var renderHint = isPixelFont ? TextRenderingHint.SingleBitPerPixelGridFit : TextRenderingHint.AntiAliasGridFit;

    StringFormat fmt = new StringFormat(StringFormat.GenericTypographic);
    fmt.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;

    Bitmap probe = new Bitmap(512, 512, PixelFormat.Format32bppArgb);
    Graphics pg = Graphics.FromImage(probe);
    pg.TextRenderingHint = renderHint;
    SizeF ms = pg.MeasureString("国", cnFont, 512, fmt);
    pg.Dispose();
    probe.Dispose();

    float actualH = ms.Height;
    if (actualH > 0 && targetH > 0 && Math.Abs(actualH - targetH) > 2)
    {
        float scale = (float)(targetH - 2) / actualH;
        renderEm = em * scale;
        if (renderEm < 6) renderEm = 6;
        cnFont.Dispose();
        cnFont = new Font(family, renderEm, FontStyle.Regular, GraphicsUnit.Pixel);

        probe = new Bitmap(512, 512, PixelFormat.Format32bppArgb);
        pg = Graphics.FromImage(probe);
        pg.TextRenderingHint = renderHint;
        ms = pg.MeasureString("国", cnFont, 512, fmt);
        pg.Dispose();
        probe.Dispose();
    }

    int cnW = Math.Max((int)Math.Ceiling(ms.Width) + 2, 2);
    // 纹理格子高度保持和原始字形一致，确保游戏布局不变
    int cnH = origGlyphH;
    // 实际渲染内容的高度（较小）
    int renderH = Math.Max((int)Math.Ceiling(ms.Height) + 2, targetH);
    // 在格子内垂直居中的偏移
    int vertPad = Math.Max((cnH - renderH) / 2, 0);

    int origMaxX = 0, origMaxY = 0;
    foreach (var og in origGlyphs)
    {
        int rx = og.SourceX + og.SourceWidth;
        int ry = og.SourceY + og.SourceHeight;
        if (rx > origMaxX) origMaxX = rx;
        if (ry > origMaxY) origMaxY = ry;
    }

    int cnStartY = origMaxY + 2;
    int cnCols = Math.Min(2048 / cnW, cjkChars.Count);
    if (cnCols < 1) cnCols = 1;
    int cnRows = (cjkChars.Count + cnCols - 1) / cnCols;

    int aw = Pow2(Math.Max(origMaxX, cnCols * cnW));
    int ah = Pow2(cnStartY + cnRows * cnH);

    if (aw > 4096 || ah > 8192)
    {
        results.Add("  SKIP " + fname + ": 纹理太大 " + aw + "x" + ah);
        cnFont.Dispose();
        continue;
    }

    Bitmap maskBmp = new Bitmap(aw, ah, PixelFormat.Format32bppArgb);
    Graphics mg = Graphics.FromImage(maskBmp);
    mg.CompositingMode = CompositingMode.SourceCopy;
    mg.SmoothingMode = SmoothingMode.None;
    mg.InterpolationMode = InterpolationMode.NearestNeighbor;
    mg.PixelOffsetMode = PixelOffsetMode.Half;
    mg.Clear(Color.Transparent);

    // 只复制非 CJK 的原始字形（CJK 字符将统一用新字体渲染）
    foreach (var og in origGlyphs)
    {
        if (cjkSet.Contains((int)og.Character)) continue;

        int srcX = tpiOffX + og.SourceX;
        int srcY = tpiOffY + og.SourceY;
        int gw = og.SourceWidth;
        int gh = og.SourceHeight;
        if (gw <= 0 || gh <= 0) continue;
        if (srcX + gw > origAtlasBmp.Width || srcY + gh > origAtlasBmp.Height) continue;

        var srcRect = new Rectangle(srcX, srcY, gw, gh);
        var dstRect = new Rectangle(og.SourceX, og.SourceY, gw, gh);
        mg.DrawImage(origAtlasBmp, dstRect, srcRect, GraphicsUnit.Pixel);
    }

    // 把原始字体中已有的 CJK 字符也加入渲染列表
    foreach (var og in origGlyphs)
    {
        int ch = (int)og.Character;
        if (cjkSet.Contains(ch) && !cjkChars.Contains(ch))
            cjkChars.Add(ch);
    }
    cjkChars.Sort();

    mg.CompositingMode = CompositingMode.SourceOver;
    mg.TextRenderingHint = renderHint;
    mg.SmoothingMode = isPixelFont ? SmoothingMode.None : SmoothingMode.HighQuality;
    SolidBrush brush = new SolidBrush(Color.White);

    List<UndertaleFont.Glyph> cnGlyphs = new List<UndertaleFont.Glyph>();
    int ci = 0, ri = 0;
    foreach (int cp in cjkChars)
    {
        string s = ((char)cp).ToString();
        int px = ci * cnW;
        int py = cnStartY + ri * cnH;

        SizeF sz = mg.MeasureString(s, cnFont, 256, fmt);
        mg.DrawString(s, cnFont, brush, px, py + vertPad, fmt);

        int gw = Math.Max((int)Math.Ceiling(sz.Width), 1);
        var gl = new UndertaleFont.Glyph();
        gl.Character = (ushort)cp;
        gl.SourceX = (ushort)px;
        gl.SourceY = (ushort)py;
        gl.SourceWidth = (ushort)gw;
        gl.SourceHeight = (ushort)cnH;
        gl.Shift = (short)(gw + 1);
        gl.Offset = 0;
        cnGlyphs.Add(gl);

        ci++;
        if (ci >= cnCols) { ci = 0; ri++; }
    }

    mg.Dispose();
    brush.Dispose();
    cnFont.Dispose();
    origAtlasBmp.Dispose();

    // RGB 强制白色，保持 alpha
    Bitmap atlas = maskBmp;
    var fixRect = new Rectangle(0, 0, atlas.Width, atlas.Height);
    var fixData = atlas.LockBits(fixRect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
    int fixStride = Math.Abs(fixData.Stride);
    int fixCount = fixStride * fixData.Height;
    byte[] fixPx = new byte[fixCount];
    System.Runtime.InteropServices.Marshal.Copy(fixData.Scan0, fixPx, 0, fixCount);
    for (int i = 0; i < fixCount; i += 4)
    {
        fixPx[i]     = 255;
        fixPx[i + 1] = 255;
        fixPx[i + 2] = 255;
    }
    System.Runtime.InteropServices.Marshal.Copy(fixPx, 0, fixData.Scan0, fixCount);
    atlas.UnlockBits(fixData);

    // 合并字形：非 CJK 用原始字形，CJK 一律用新渲染的字形
    f.Glyphs.Clear();
    foreach (var og in origGlyphs)
    {
        if (!cjkSet.Contains((int)og.Character))
            f.Glyphs.Add(og);
    }
    foreach (var cg in cnGlyphs)
        f.Glyphs.Add(cg);

    // ---- 创建新纹理 ----
    byte[] pngData;
    using (MemoryStream stream = new MemoryStream())
    {
        atlas.Save(stream, ImageFormat.Png);
        pngData = stream.ToArray();
    }

    var tex = new UndertaleEmbeddedTexture();
    tex.TextureData = new UndertaleEmbeddedTexture.TexData();

    var tdType = tex.TextureData.GetType();
    var imageProp = tdType.GetProperty("Image");
    Type gmImageType = imageProp.PropertyType;
    var staticFlags = BindingFlags.Static | BindingFlags.Public;
    var fromPng = gmImageType.GetMethod("FromPng", staticFlags, null, new Type[] { typeof(byte[]), typeof(bool) }, null);
    object gmImage = fromPng.Invoke(null, new object[] { pngData, false });
    imageProp.SetValue(tex.TextureData, gmImage);

    var widthProp = tdType.GetProperty("Width");
    var heightProp = tdType.GetProperty("Height");
    if (widthProp != null && widthProp.CanWrite) widthProp.SetValue(tex.TextureData, aw);
    if (heightProp != null && heightProp.CanWrite) heightProp.SetValue(tex.TextureData, ah);

    // 复制原始纹理的 Scaled / GeneratedMips
    var texType = tex.GetType();
    uint origScaled = 1;
    uint origMips = 0;
    if (origEmbTex != null)
    {
        try { origScaled = (uint)texType.GetProperty("Scaled").GetValue(origEmbTex); } catch { }
        try { origMips = (uint)texType.GetProperty("GeneratedMips").GetValue(origEmbTex); } catch { }
    }
    var scaledProp = texType.GetProperty("Scaled");
    if (scaledProp != null && scaledProp.CanWrite) scaledProp.SetValue(tex, origScaled);
    var mipsProp = texType.GetProperty("GeneratedMips");
    if (mipsProp != null && mipsProp.CanWrite) mipsProp.SetValue(tex, origMips);

    Data.EmbeddedTextures.Add(tex);

    // ==== 关键修复：将新纹理注册到和原始纹理相同的 TextureGroupInfo ====
    addTextureToGroup(origTexGroup, tex);
    addFontToGroup(origTexGroup, f);

    var item = new UndertaleTexturePageItem();
    item.SourceX = 0;
    item.SourceY = 0;
    item.SourceWidth = (ushort)aw;
    item.SourceHeight = (ushort)ah;
    item.TargetX = 0;
    item.TargetY = 0;
    item.TargetWidth = (ushort)aw;
    item.TargetHeight = (ushort)ah;
    item.BoundingWidth = (ushort)aw;
    item.BoundingHeight = (ushort)ah;
    item.TexturePage = tex;
    Data.TexturePageItems.Add(item);

    f.Texture = item;
    if (cjkChars.Count > 0)
    {
        f.RangeStart = (ushort)Math.Min((int)f.RangeStart, cjkChars.First());
        f.RangeEnd = (uint)Math.Max((int)f.RangeEnd, cjkChars.Last());
    }

    atlas.Dispose();
    patched++;
    results.Add("  OK " + fname + " (em=" + em + " renderEm=" + renderEm.ToString("F1")
        + " glyphH=" + cnH + "/" + origGlyphH + " AA=" + origAA + " group=" + groupName
        + " orig+cn=" + origGlyphs.Count + "+" + cnGlyphs.Count + " atlas=" + aw + "x" + ah + ")");
}

pfc.Dispose();

ScriptMessage("修补完成！\n\n" + String.Join("\n", results)
    + "\n\n共修补 " + patched + "/" + Data.Fonts.Count + " 个字体"
    + "\n中文字符数: " + cjkChars.Count
    + "\n\n请 File → Save 保存");
