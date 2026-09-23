using QRCoder;

namespace CleanC.App;

// The visual style may change, but every frame retains the complete standard QR payload.
internal sealed class StandardParticleQrRenderer
{
    readonly bool[][] modules;
    readonly (double X, double Y, double Radius, double Phase)[] stars;
    public int ModuleCount => modules.Length;

    public StandardParticleQrRenderer(string url)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.L);
        modules = data.ModuleMatrix.Select(row => Enumerable.Range(0, row.Length).Select(i => row[i]).ToArray()).ToArray();
        var random = new Random(1658);
        stars = new (double, double, double, double)[92];
        for (var i = 0; i < stars.Length; i++)
        {
            // A restrained circular field; low opacity keeps the QR contrast intact.
            var angle = i * Math.Tau / stars.Length + .13;
            var radius = 0.45 + random.NextDouble() * .035;
            stars[i] = (0.5 + Math.Cos(angle) * radius, 0.5 + Math.Sin(angle) * radius,
                .85 + random.NextDouble() * 2.4, random.NextDouble() * Math.Tau);
        }
    }

    public void Render(byte[] pixels, int size, double seconds, bool dark = false)
    {
        if (pixels.Length != size * size * 4) throw new ArgumentException("Invalid raster buffer.", nameof(pixels));
        Array.Clear(pixels);
        var cell = Math.Floor(size * .9 / ModuleCount);
        var start = (size - cell * ModuleCount) / 2;
        for (var y = 0; y < ModuleCount; y++)
        for (var x = 0; x < ModuleCount; x++)
        {
            if (!modules[y][x]) continue;
            var hash = unchecked((uint)(x * 73856093 ^ y * 19349663));
            var phase = (hash % 628) / 100d;
            // Varied-size round modules keep the symbol readable; movement is small enough
            // that finder, timing, alignment and data modules remain in their QR cells.
            var radius = cell * (.555 + hash % 9 * .009) * (.988 + .012 * Math.Sin(seconds * 1.2 + phase));
            // All hues retain low luminance on the light canvas: colour must not sacrifice decoding.
            var blend = Math.Clamp((x+y)/(2d*ModuleCount),0,1);
            var blue = dark ? (byte)230 : (byte)(102+blend*26);
            var red = dark ? (byte)222 : (byte)(23+blend*28);
            var green = dark ? (byte)244 : (byte)(73-blend*23);
            if (Functional(x,y))
                Box(pixels,size,start+x*cell,start+y*cell,cell,red,green,blue);
            else
                Disc(pixels,size,start + (x+.5)*cell,start + (y+.5)*cell,radius,red,green,blue,1);
        }
        // Animated stars remain faint enough that the QR contrast survives.
        foreach (var star in stars)
        {
            var sway = .7 * Math.Sin(seconds * .7 + star.Phase);
            var alpha = .40 + .28 * (.5 + .5 * Math.Sin(seconds * 1.8 + star.Phase));
            Disc(pixels,size,(star.X + sway/size)*size,star.Y*size,
                star.Radius * size / 600d, dark ? (byte)136 : (byte)57,
                dark ? (byte)190 : (byte)128, dark ? (byte)255 : (byte)211, alpha);
        }
    }

    bool Functional(int x,int y)
    {
        const int q=4;
        // QRCoder's matrix includes its four-module quiet zone.
        var finder = (x>=q&&x<q+7&&y>=q&&y<q+7) ||
                     (x>=ModuleCount-q-7&&x<ModuleCount-q&&y>=q&&y<q+7) ||
                     (x>=q&&x<q+7&&y>=ModuleCount-q-7&&y<ModuleCount-q);
        return finder || (x==q+6 && y>=q+8 && y<ModuleCount-q-8) ||
            (y==q+6 && x>=q+8 && x<ModuleCount-q-8);
    }

    static void Box(byte[] pixels,int size,double left,double top,double side,byte red,byte green,byte blue)
    {
        var minX=Math.Max(0,(int)Math.Ceiling(left-.5));var maxX=Math.Min(size,(int)Math.Ceiling(left+side-.5));
        var minY=Math.Max(0,(int)Math.Ceiling(top-.5));var maxY=Math.Min(size,(int)Math.Ceiling(top+side-.5));
        for(var y=minY;y<maxY;y++)for(var x=minX;x<maxX;x++)
        {
            var at=(y*size+x)*4;pixels[at]=blue;pixels[at+1]=green;pixels[at+2]=red;pixels[at+3]=255;
        }
    }

    static void Disc(byte[] pixels, int size, double cx, double cy, double radius,
        byte red, byte green, byte blue, double opacity)
    {
        var x0 = Math.Max(0,(int)Math.Floor(cx-radius-1)); var x1 = Math.Min(size-1,(int)Math.Ceiling(cx+radius+1));
        var y0 = Math.Max(0,(int)Math.Floor(cy-radius-1)); var y1 = Math.Min(size-1,(int)Math.Ceiling(cy+radius+1));
        for (var y = y0; y <= y1; y++)
        for (var x = x0; x <= x1; x++)
        {
            var distance = Math.Sqrt((x+.5-cx)*(x+.5-cx)+(y+.5-cy)*(y+.5-cy));
            var amount = opacity * Math.Clamp(radius + .45 - distance,0,1);
            if(amount<=0)continue;
            var at=(y*size+x)*4; var remaining=1-amount;
            pixels[at]=(byte)Math.Clamp(blue*amount+pixels[at]*remaining,0,255);
            pixels[at+1]=(byte)Math.Clamp(green*amount+pixels[at+1]*remaining,0,255);
            pixels[at+2]=(byte)Math.Clamp(red*amount+pixels[at+2]*remaining,0,255);
            pixels[at+3]=(byte)Math.Clamp(255*amount+pixels[at+3]*remaining,0,255);
        }
    }
}
