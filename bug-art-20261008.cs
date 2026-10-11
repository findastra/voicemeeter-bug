using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace VoicemeeterBug {
    internal static class BugArt {
        internal const int PetSize=72;
        static readonly Dictionary<string,Bitmap> frames=new Dictionary<string,Bitmap>();
        static readonly Dictionary<string,Rectangle> bounds=new Dictionary<string,Rectangle>();
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);
        static Bitmap Frame(string mood) {
            Bitmap frame;
            if(frames.TryGetValue(mood,out frame))return frame;
            using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("VoicemeeterBug."+mood)) {
                if(stream==null)throw new InvalidOperationException("Missing embedded Bug art: "+mood);
                using(var image=Image.FromStream(stream))frame=new Bitmap(image);
            }
            int left=frame.Width,top=frame.Height,right=0,bottom=0;
            for(int y=0;y<frame.Height;y++)for(int x=0;x<frame.Width;x++)if(frame.GetPixel(x,y).A>0){left=Math.Min(left,x);top=Math.Min(top,y);right=Math.Max(right,x+1);bottom=Math.Max(bottom,y+1);}
            frames[mood]=frame;bounds[mood]=Rectangle.FromLTRB(left,top,right,bottom);return frame;
        }
        internal static Icon MakeIcon() {
            using(var bmp=new Bitmap(64,64))using(var g=Graphics.FromImage(bmp)) {
                g.Clear(Color.Transparent);g.ScaleTransform(64f/PetSize,64f/PetSize);Draw(g,0,true,false);
                var handle=bmp.GetHicon();try{return (Icon)Icon.FromHandle(handle).Clone();}finally{DestroyIcon(handle);}
            }
        }
        internal static void Draw(Graphics g,long phase,bool connected,bool repairing) {
            string mood=repairing?"happy":phase%5100>4860?"blink":"idle";
            var image=Frame(mood);var source=bounds[mood];
            float size=58.75f,scale=size/Math.Max(source.Width,source.Height);
            float width=source.Width*scale,height=source.Height*scale;
            float bob=(float)Math.Sin(phase/800.0)*.5f;
            var saved=g.Save();g.InterpolationMode=InterpolationMode.NearestNeighbor;g.PixelOffsetMode=PixelOffsetMode.Half;
            g.DrawImage(image,new RectangleF((PetSize-width)/2,(PetSize-height)/2+bob,width,height),source,GraphicsUnit.Pixel);g.Restore(saved);
        }
        internal static void Star(Graphics g,int size) {
            var saved=g.Save();g.ScaleTransform(size/40f,size/40f);g.SmoothingMode=SmoothingMode.AntiAlias;
            var points=new PointF[10];
            for(int i=0;i<10;i++){double a=-Math.PI/2+i*Math.PI/5;double r=i%2==0?18:10.4;points[i]=new PointF(20+(float)(Math.Cos(a)*r),20+(float)(Math.Sin(a)*r));}
            using(var path=new GraphicsPath()) {
                for(int i=0;i<10;i++){
                    var before=points[(i+9)%10];var p=points[i];var next=points[(i+1)%10];
                    var a=new PointF(p.X+(before.X-p.X)*.24f,p.Y+(before.Y-p.Y)*.24f);
                    var b=new PointF(p.X+(next.X-p.X)*.24f,p.Y+(next.Y-p.Y)*.24f);
                    if(i==0)path.StartFigure();else path.AddLine(path.GetLastPoint(),a);
                    path.AddBezier(a,p,p,b);
                }
                path.CloseFigure();
                using(var shade=new PathGradientBrush(path)){shade.CenterPoint=new PointF(17,13);shade.CenterColor=Color.FromArgb(255,246,169);shade.SurroundColors=new[]{Color.FromArgb(244,178,55)};g.FillPath(shade,path);}
                using(var edge=new Pen(Color.FromArgb(125,82,37),2.1f)){edge.LineJoin=LineJoin.Round;g.DrawPath(edge,path);}
            }
            using(var shine=new Pen(Color.FromArgb(255,255,219),2)){shine.StartCap=shine.EndCap=LineCap.Round;g.DrawLine(shine,16,11,18,8);}
            using(var ink=new SolidBrush(Color.FromArgb(69,46,38))){g.FillEllipse(ink,14,18,3.1f,4.4f);g.FillEllipse(ink,23,18,3.1f,4.4f);}
            using(var smile=new Pen(Color.FromArgb(69,46,38),1.2f))g.DrawArc(smile,18,22,4,3,0,180);
            using(var cheek=new SolidBrush(Color.FromArgb(239,157,91))){g.FillEllipse(cheek,10,23,5,2.6f);g.FillEllipse(cheek,26,23,5,2.6f);}
            Sparkle(g,0,8,3);Sparkle(g,40,4,2.3f);Sparkle(g,39,32,3.1f);Sparkle(g,3,34,1.6f);g.Restore(saved);
        }
        static void Sparkle(Graphics g,float x,float y,float r){using(var brush=new SolidBrush(Color.FromArgb(255,245,174))){g.FillPolygon(brush,new[]{new PointF(x,y-r),new PointF(x+r*.32f,y-r*.32f),new PointF(x+r,y),new PointF(x+r*.32f,y+r*.32f),new PointF(x,y+r),new PointF(x-r*.32f,y+r*.32f),new PointF(x-r,y),new PointF(x-r*.32f,y-r*.32f)});}}
    }
}
