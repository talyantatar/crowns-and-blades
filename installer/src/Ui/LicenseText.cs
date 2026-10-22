// The license page: the LICENSE.txt shipped in the payload (the repository's LICENSE), else the MIT text below, plus the
// not-affiliated notice.
using System.Linq;
using CK3xBL.Setup.Util;

namespace CK3xBL.Setup.Ui;

internal static class LicenseText
{
    public const string Mit =
        "MIT License\r\n\r\n" +
        "Copyright (c) 2026 TaLyAn\r\n" +
        "Based on ck3-bannerlord-bridge, Copyright (c) 2026 graff0n\r\n\r\n" +
        "Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation " +
        "files (the \"Software\"), to deal in the Software without restriction, including without limitation the rights to use, copy, " +
        "modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software " +
        "is furnished to do so, subject to the following conditions:\r\n\r\n" +
        "The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.\r\n\r\n" +
        "THE SOFTWARE IS PROVIDED \"AS IS\", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE " +
        "WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR " +
        "COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, " +
        "ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.\r\n";

    public static string For(Payload payload)
    {
        string license = Mit;
        var file = payload.Manifest.Files.FirstOrDefault(f => f.Path == "app/LICENSE.txt" || f.Path == "app/LICENSE");
        if (file is not null)
        {
            try { license = FileOps.ReadUtf8(payload.ReadAll(file), out _).Replace("\r\n", "\n").Replace("\n", "\r\n"); }
            catch (System.IO.InvalidDataException) { }
        }
        return license.TrimEnd() + "\r\n\r\n" +
               "----------------------------------------------------------------------\r\n\r\n" +
               Product.Disclaimer + "\r\n\r\n" +
               "This is a beta. It was built for Crusader Kings III 1.20 and Mount & Blade II: Bannerlord 1.4.8; other versions may " +
               "break and are not blocked. It changes no game files: it adds a CK3 mod to your Documents folder and a module to " +
               "Bannerlord's Modules folder, and both are removed again by the uninstaller.\r\n";
    }
}
