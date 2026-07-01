using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace Cmso.Biometric.Agent.Tray
{
    public class NotificationForm : Form
    {
        private readonly string _title;
        private readonly string _message;
        private readonly Color _accentColor;
        private readonly Image? _logo;

        private readonly System.Windows.Forms.Timer _animTimer;
        private readonly System.Windows.Forms.Timer _closeTimer;
        private int _targetY;
        private int _currentY;
        private double _opacity = 0.0;
        private bool _isClosing = false;

        public NotificationForm(string title, string message, Color accentColor)
        {
            _title = title;
            _message = message;
            _accentColor = accentColor;

            // Carrega o logotipo
            var pngPath = Path.Combine(AppContext.BaseDirectory, "cmso_icone.png");
            if (File.Exists(pngPath))
            {
                try
                {
                    _logo = Image.FromFile(pngPath);
                }
                catch
                {
                    // Fallback se falhar
                }
            }

            // Configurações do Form
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            DoubleBuffered = true;
            Size = new Size(340, 85);
            BackColor = Color.FromArgb(28, 28, 32); // Cinza escuro premium
            Opacity = 0;

            // Timer de animação (slide-in / fade-in)
            _animTimer = new System.Windows.Forms.Timer { Interval = 15 };
            _animTimer.Tick += OnAnimTick;

            // Timer para fechar automaticamente
            _closeTimer = new System.Windows.Forms.Timer { Interval = 4000 };
            _closeTimer.Tick += (s, e) => CloseNotification();

            // Eventos
            Click += (s, e) => CloseNotification();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            // Posiciona no canto inferior direito da tela principal
            var workingArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1024, 768);
            int x = workingArea.Right - Width - 15;
            _targetY = workingArea.Bottom - Height - 15;
            _currentY = _targetY + 40; // Começa 40px abaixo para efeito de subida

            Location = new Point(x, _currentY);

            _animTimer.Start();
            _closeTimer.Start();
        }

        private void OnAnimTick(object? sender, EventArgs e)
        {
            if (!_isClosing)
            {
                // Slide-in e Fade-in
                if (_currentY > _targetY)
                {
                    _currentY -= 2;
                }
                if (_opacity < 0.95)
                {
                    _opacity += 0.08;
                }

                Location = new Point(Location.X, _currentY);
                Opacity = _opacity;

                if (_currentY <= _targetY && _opacity >= 0.95)
                {
                    _animTimer.Stop();
                }
            }
            else
            {
                // Slide-out e Fade-out ao fechar
                _currentY += 2;
                _opacity -= 0.08;

                Location = new Point(Location.X, _currentY);
                Opacity = _opacity;

                if (_opacity <= 0)
                {
                    _animTimer.Stop();
                    Close();
                }
            }
        }

        private void CloseNotification()
        {
            if (_isClosing) return;
            _isClosing = true;
            _closeTimer.Stop();
            _animTimer.Start(); // Re-inicia o timer de animação para o slide-out
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // Desenha borda arredondada e fundo
            using (var path = CreateRoundedRectanglePath(new Rectangle(0, 0, Width - 1, Height - 1), 6))
            {
                // Preenchimento com degradê sutil
                using (var brush = new LinearGradientBrush(ClientRectangle, Color.FromArgb(32, 32, 38), Color.FromArgb(24, 24, 28), 90f))
                {
                    g.FillPath(brush, path);
                }
                
                // Borda fina
                using (var pen = new Pen(Color.FromArgb(50, 50, 60), 1))
                {
                    g.DrawPath(pen, path);
                }
            }

            // Barra de destaque colorida na esquerda (indica o status)
            using (var brush = new SolidBrush(_accentColor))
            {
                g.FillRectangle(brush, 0, 0, 5, Height);
            }

            // Desenha o logotipo da empresa
            int logoSize = 48;
            int logoX = 15;
            int logoY = (Height - logoSize) / 2;

            if (_logo != null)
            {
                g.DrawImage(_logo, new Rectangle(logoX, logoY, logoSize, logoSize));
            }
            else
            {
                // Fallback caso não ache a imagem
                g.FillEllipse(Brushes.DimGray, logoX, logoY, logoSize, logoSize);
            }

            // Textos
            int textX = logoX + logoSize + 15;
            
            // Título
            using (var titleFont = new Font("Segoe UI", 9.5f, FontStyle.Bold))
            {
                g.DrawString(_title, titleFont, Brushes.White, textX, 16);
            }

            // Mensagem
            using (var messageFont = new Font("Segoe UI", 8.5f, FontStyle.Regular))
            {
                var textRect = new RectangleF(textX, 38, Width - textX - 15, Height - 45);
                g.DrawString(_message, messageFont, Brushes.LightGray, textRect);
            }
        }

        private static GraphicsPath CreateRoundedRectanglePath(Rectangle rect, int cornerRadius)
        {
            var path = new GraphicsPath();
            int diameter = cornerRadius * 2;
            var size = new Size(diameter, diameter);
            
            path.AddArc(new Rectangle(rect.Location, size), 180, 90);
            path.AddArc(new Rectangle(new Point(rect.Right - diameter, rect.Y), size), 270, 90);
            path.AddArc(new Rectangle(new Point(rect.Right - diameter, rect.Bottom - diameter), size), 0, 90);
            path.AddArc(new Rectangle(new Point(rect.X, rect.Bottom - diameter), size), 90, 90);
            path.CloseFigure();
            
            return path;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _logo?.Dispose();
                _animTimer.Dispose();
                _closeTimer.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
