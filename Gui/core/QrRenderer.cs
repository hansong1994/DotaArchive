using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;

namespace DotaArchive {
 internal static class QrRenderer {
  // QRCoder.GetLineByLineGraphic(1) uses two characters per square module.
  // Return only complete square symbols with the original four-module quiet zone.
  internal static Bitmap Render(IList<string> rows) {
   if(rows == null || rows.Count < 29 || rows.Count > 185 || (rows.Count - 29) % 4 != 0) return null;
   int modules = rows.Count;
   bool anyDark = false;
   for(int y=0;y<modules;y++) {
    if(rows[y].Length != modules*2) return null;
    for(int x=0;x<modules;x++) {
     char a=rows[y][x*2], b=rows[y][x*2+1];
     if(a != b || (a != ' ' && a != '\u2588')) return null;
     bool dark=a=='\u2588';
     if(dark && (x<4 || y<4 || x>=modules-4 || y>=modules-4)) return null;
     anyDark |= dark;
    }
   }
   if(!anyDark) return null;
   const int scale=6;
   Bitmap image=new Bitmap(modules*scale,modules*scale,PixelFormat.Format24bppRgb);
   using(Graphics g=Graphics.FromImage(image)) {
    g.Clear(Color.White);
    for(int y=0;y<modules;y++) for(int x=0;x<modules;x++)
     if(rows[y][x*2]=='\u2588') g.FillRectangle(Brushes.Black,x*scale,y*scale,scale,scale);
   }
   return image;
  }
 }
}
