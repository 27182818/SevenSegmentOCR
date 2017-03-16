using System;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Collections.Generic;
using System.Diagnostics;

namespace SevenSegmentOCR
{
    public class Coord
    {
        public int x, y;
        public Coord(int a, int b)
        {
            x = a;
            y = b;
        }
    }
    public class ConnComp
    {
        public long centX;
        public long centY;
        public int width;
        public int height;
        public int cluster;
        public List<Coord> pixels = new List<Coord>();
        public void CalcValues()
        {
            long x = 0;
            long y = 0;
            int minX = int.MaxValue;
            int maxX = int.MinValue;
            int minY = int.MaxValue;
            int maxY = int.MinValue;
            foreach (Coord pixel in pixels)
            {
                x += pixel.x;
                y += pixel.y;
                if (pixel.x < minX)
                    minX = pixel.x;
                if (pixel.x > maxX)
                    maxX = pixel.x;
                if (pixel.y < minY)
                    minY = pixel.y;
                if (pixel.y > maxY)
                    maxY = pixel.y;
            }
            centX = x / pixels.Count;
            centY = y / pixels.Count;
            width = maxX - minX;
            height = maxY - minY;
        }
    }

    public partial class MainPage : Page, INotifyPropertyChanged
    {
        /*
         * Recognize digits in a seven-segment display.
         * We assume that we know the structure of the display, as well as the number of digits which are displayed.
         * 
         * We do this in the following manner:
         * 1) An image is read. The greyscale value of each pixel is found and is then thresholded to yield a boolean array
         * 2) Connected components are found, and those with fewer than a specified number of pixels are discarded. It is expected that
         * each remaining component is a "segment" of a seven-segment display's digit
         * 3) k-means clustering is used to cluster the segments. It is expected that each cluster represents a digit
         * 4) We identify each cluster as its corrosponding digit based on simple rules
         */
        const int k = 16;//the number of numbers which we have to identify
        const int minPixelCount = 10;//we ignore any component with fewer than this number of pixels
        const int numRows = 4;//the number of rows of digits in the image, used in the intialization step of the k-means clustering
        const int numColumns = 4;//the number of columns of digits in the image, used in the intialization step of the k-means clustering
        const double threshold = 150;//threshold a greyscale pixel to white if above this value, black otherwise
        const bool digitsAreLighter = true;//are the digits lighter or darker than the background?
        WriteableBitmap srcBitmap;
        List<ConnComp> connComps = new List<ConnComp>();
        bool[,] connectedPixels;
        Image image;
        long ms;

        private string _OCRText = "...";
        public string OCRText
        {
            get
            {
                return _OCRText;
            }
            set
            {
                _OCRText = value;
                RaisePropertyChanged("OCRText");
            }
        }

        private double SquaredEuclidDist(Coord a, Coord b)
        {
            return Math.Pow(a.x - b.x, 2) + Math.Pow(a.y - b.y, 2);
        }
        private async Task GetBWImage()
        {
            //first, read an image
            FileOpenPicker fileOpenPicker = new FileOpenPicker();
            fileOpenPicker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
            fileOpenPicker.FileTypeFilter.Add(".jpg");
            fileOpenPicker.ViewMode = PickerViewMode.Thumbnail;
            var file = await fileOpenPicker.PickSingleFileAsync();
            if (file == null)
            {
                return;
            }
            ms = DateTime.Now.Ticks;
            BitmapDecoder decoder = null;
            using (IRandomAccessStreamWithContentType stream = await file.OpenReadAsync())
            {
                decoder = await BitmapDecoder.CreateAsync(stream);
            }
            BitmapFrame bitmapFrame = await decoder.GetFrameAsync(0);
            PixelDataProvider dataProvider =
                await bitmapFrame.GetPixelDataAsync(BitmapPixelFormat.Bgra8,
                                                    BitmapAlphaMode.Premultiplied,
                                                    new BitmapTransform(),
                                                    ExifOrientationMode.RespectExifOrientation,
                                                    ColorManagementMode.ColorManageToSRgb);
            byte[] pixels = dataProvider.DetachPixelData();
            WriteableBitmap bitmap = new WriteableBitmap((int)bitmapFrame.PixelWidth,
                                                         (int)bitmapFrame.PixelHeight);
            using (Stream pixelStream = bitmap.PixelBuffer.AsStream())
            {
                await pixelStream.WriteAsync(pixels, 0, pixels.Length);
            }
            image = new Image();
            image.Source = bitmap;
            srcBitmap = image.Source as WriteableBitmap;
            byte[] srcPixels = new byte[4 * srcBitmap.PixelWidth * srcBitmap.PixelHeight];
            using (Stream pixelStream = srcBitmap.PixelBuffer.AsStream())
            {
                await pixelStream.ReadAsync(srcPixels, 0, srcPixels.Length);
            }
            //we have finally read the image. Now, we create the corresponding boolean array
            connectedPixels = new bool[srcBitmap.PixelWidth, srcBitmap.PixelHeight];
            int x = 0;
            int y = 0;
            for (int i = 0; i < srcPixels.Length; i += 4)
            {
                double b = srcPixels[i] / 255.0;
                double g = srcPixels[i + 1] / 255.0;
                double r = srcPixels[i + 2] / 255.0;
                double e = (0.21 * r + 0.71 * g + 0.07 * b) * 255;//e is the greyscale value of the pixel
                //now, threshold the greyscal pixel to get black and white
                if (e < threshold)
                {
                    connectedPixels[x, y] = true ^ digitsAreLighter;
                }
                else
                {
                    connectedPixels[x, y] = false ^ digitsAreLighter;
                }
                if (x < srcBitmap.PixelWidth - 1)
                    x++;
                else
                {
                    x = 0;
                    y++;
                }
            }
        }
        
        private async Task IDDigitsInPicture()
        {
            await GetBWImage();
            Debug.WriteLine("black&white image found at " + (DateTime.Now.Ticks - ms)/TimeSpan.TicksPerMillisecond + " ms");
            FindConnComp();
            Debug.WriteLine("connected components found at " + (DateTime.Now.Ticks - ms) / TimeSpan.TicksPerMillisecond + " ms");
            KMeansCluster();
            Debug.WriteLine("k-means clustering finished at " + (DateTime.Now.Ticks - ms) / TimeSpan.TicksPerMillisecond + " ms");
            OCRText = String.Empty;
            for (int iter = 0; iter < k; iter++)
            {
                List<ConnComp> parts = new List<ConnComp>();
                foreach (ConnComp connComp in connComps)
                {
                    if (connComp.cluster == iter)
                    {
                        parts.Add(connComp);
                    }
                }
                string num = IdentifyNumber(parts);
                Debug.WriteLine(num);
                OCRText += num;
            }
            Debug.WriteLine("digits identified at " + (DateTime.Now.Ticks - ms) / TimeSpan.TicksPerMillisecond + " ms");
        }

        private void KMeansCluster()
        {
            Debug.WriteLine("starting k-means clustering");
            Coord[] centroids = new Coord[k];
            long[] sumX = new long[k];//must be long - using int instead results in overflow problems
            long[] sumY = new long[k];
            int[] count = new int[k];
            //initialization step:
            //We can dramatically reduce the number of iterations if we perform the initialization based upon our knowledge
            //of how the digits are distributed spatially.
            //Here, we assume that the digits are distributed evenly in a grid
            for (int iterY = 0; iterY < numRows; iterY++)
            {
                for (int iterX = 0; iterX < numColumns; iterX++)
                {
                    centroids[iterX + numColumns * iterY] = new Coord((srcBitmap.PixelWidth / (2 * numColumns)) + (iterX * srcBitmap.PixelWidth / numColumns), (srcBitmap.PixelHeight / (2 * numRows)) + (iterY * srcBitmap.PixelHeight / numRows));
                }
            }
            bool converged = false;
            int step = 1;//used for debugging purposes only
            while (!converged)
            {
                Debug.WriteLine(step++ + "th iteration");
                //assignment step:
                converged = true;
                foreach (ConnComp connComp in connComps)
                {
                    double minDist = double.MaxValue;
                    int cluster = k;
                    for (int iter = 0; iter < k; iter++)
                    {
                        double dist = SquaredEuclidDist(new Coord((int)connComp.centX, (int)connComp.centY), centroids[iter]);
                        if (dist < minDist)
                        {
                            cluster = iter;
                            minDist = dist;
                        }
                    }
                    if (connComp.cluster != cluster)
                        converged = false;
                    connComp.cluster = cluster;
                }
                //update step:
                for (int iter = 0; iter < k; iter++)
                {
                    count[iter] = 0;
                    sumX[iter] = 0;
                    sumY[iter] = 0;
                }
                foreach (ConnComp connComp in connComps)
                {
                    count[connComp.cluster]++;
                    sumX[connComp.cluster] += connComp.centX;
                    sumY[connComp.cluster] += connComp.centY;
                }
                for (int iter = 0; iter < k; iter++)
                {
                    if (count[iter] == 0)
                    {
                        converged = true;
                        Debug.WriteLine("aborting k-means clustering due to empty cluster!");
                        break;
                    }
                    centroids[iter] = new Coord((int)(sumX[iter] / (count[iter])), (int)(sumY[iter] / (count[iter])));
                }
            }
            Debug.WriteLine("finished k-means clustering");
        }

        private string IdentifyNumber(List<ConnComp> parts)
        {
            switch (parts.Count)
            {
                case 2:
                    return "1";
                case 3:
                    return "7";
                case 4:
                    return "4";
                case 5:
                    List<ConnComp> vertComp = new List<ConnComp>();
                    ConnComp horComp = new ConnComp();
                    foreach (ConnComp connComp in parts)
                    {
                        if (connComp.height > connComp.width)
                            vertComp.Add(connComp);
                        else
                            horComp = connComp;
                    }
                    if (vertComp.Count == 3)
                        return "9";
                    int rightVertCount = 0;
                    foreach (ConnComp connComp in vertComp)
                    {
                        if (connComp.centX > horComp.centX)
                            rightVertCount++;
                    }
                    if (rightVertCount == 2)
                        return "3";
                    if ((vertComp[0].centX > vertComp[1].centX && vertComp[0].centY > vertComp[1].centY) || (vertComp[0].centX < vertComp[1].centX && vertComp[0].centY < vertComp[1].centY))
                        return "5";
                    else
                        return "2";
                case 6:
                    List<ConnComp> vertiComp = new List<ConnComp>();
                    foreach (ConnComp connComp in parts)
                    {
                        if (connComp.height > connComp.width)
                            vertiComp.Add(connComp);
                    }
                    if (vertiComp.Count == 4)
                        return "0";
                    return "6";
                case 7:
                    return "8";
                default:
                    return "failed to ID digit";
            }
        }

        private void FindConnComp()
        {
            for (int x = 0; x < connectedPixels.GetLength(0); x++)
            {
                for (int y = 0; y < connectedPixels.GetLength(1); y++)
                {
                    if (connectedPixels[x, y])
                    {
                        ConnComp connComp = ForestFire(x, y);
                        if (connComp.pixels.Count > minPixelCount)
                        {
                            connComp.CalcValues();
                            connComps.Add(connComp);
                        }
                    }
                }
            }
        }

        private ConnComp ForestFire(int x, int y)
        {
            //The forest fire algorithm, a variant of the flood fill algorithm. We use this rather than the naive, recursive
            //implementation of flood fill because that would cause the stack to overflow
            ConnComp connComp = new ConnComp();
            Queue<Coord> q = new Queue<Coord>();
            q.Enqueue(new Coord(x, y));
            while (q.Count > 0)
            {
                Coord coord = q.Dequeue();
                int w = coord.x;
                int e = coord.x;
                while (w > 0 && connectedPixels[w - 1, coord.y])
                    w--;
                while (e < connectedPixels.GetLength(0) - 1 && connectedPixels[e + 1, coord.y])
                    e++;
                for (int n = w; n <= e; n++)
                {
                    connectedPixels[n, coord.y] = false;
                    connComp.pixels.Add(new Coord(n, coord.y));
                    if (coord.y > 0 && connectedPixels[n, coord.y - 1])
                        q.Enqueue(new Coord(n, coord.y - 1));
                    if (coord.y < connectedPixels.GetLength(1) - 1 && connectedPixels[n, coord.y + 1])
                        q.Enqueue(new Coord(n, coord.y + 1));
                }
            }
            return connComp;
        }

        public MainPage()
        {
            this.InitializeComponent();
            IDDigitsInPicture();
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void RaisePropertyChanged(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        /*
         * Assuming that this is the Tzufit 1
         */
        private string PSI2Temp(int psi)
        {
            switch (psi)
            {
                case 71:
                    return "25-31";
                case 72:
                    return "25-34";
                case 73:
                    return "25-38";
                case 74:
                    return "25-42";
                case 75:
                    return "28-46";
                case 76:
                    return "32-50";
                case 77:
                    return "35-50";
                case 78:
                    return "39-50";
                case 79:
                    return "43-50";
                case 80:
                    return "47-50";
                default:
                    return "שגיאה - הPSI חורגת מן המגבלות";
            }
        }
    }
}
