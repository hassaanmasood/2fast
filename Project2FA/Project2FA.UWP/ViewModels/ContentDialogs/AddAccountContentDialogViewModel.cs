﻿using OtpNet;
using Prism.Commands;
using Prism.Logging;
using Prism.Mvvm;
using Project2FA.Core.Services.Parser;
using Project2FA.Repository.Models;
using System;
using System.Web;
using System.Windows.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Prism.Ioc;
using Project2FA.UWP.Services;
using Windows.UI.Popups;
using Template10.Services.Resources;
using ZXing.QrCode;
using ZXing.Common;
using ZXing;
using System.ComponentModel.DataAnnotations;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Project2FA.UWP.ViewModels
{
    /// <summary>
    /// View model for adding an account countent dialog
    /// </summary>
    public class AddAccountContentDialogViewModel : BindableBase
    {
        private string _qrCodeStr;
        private bool _qrCodeScan, _launchScreenClip, _isButtonEnable;
        private bool _manualInput;
        private bool _isCameraActive;
        private TwoFACodeModel _model;
        private int _selectedPivotIndex;
        private int _openingSeconds;
        private int _seconds;
        private string _secretKey;
        DispatcherTimer _dispatcherTimer;
        public ICommand ManualInputCommand { get; }
        public ICommand ScanQRCodeCommand { get; }
        public ICommand PrimaryButtonCommand { get; }
        public ICommand CameraScanCommand { get; }
        private ILoggerFacade Logger { get; }
        private IResourceService ResourceService { get; }

        /// <summary>
        /// Constructor
        /// </summary>
        public AddAccountContentDialogViewModel()
        {
            ResourceService = App.Current.Container.Resolve<IResourceService>();

            _dispatcherTimer = new DispatcherTimer();
            _dispatcherTimer.Interval = new TimeSpan(0, 0, 1); //every second        

            Model = new TwoFACodeModel();
            ManualInputCommand = new DelegateCommand(() =>
            {
                SelectedPivotIndex = 1;
                ManualInput = true;
            });
            ScanQRCodeCommand = new DelegateCommand( async() =>
            {
                OpeningSeconds = SettingsService.Instance.QRCodeScanSeconds;
                _dispatcherTimer.Tick -= OnTimedEvent;
                _dispatcherTimer.Tick += OnTimedEvent;
                Seconds = OpeningSeconds;
                _dispatcherTimer.Start();
                await ScanQRCode();
            });
            PrimaryButtonCommand = new DelegateCommand(() =>
            {
                DataService.Instance.Collection.Add(Model);
            });

            CameraScanCommand = new DelegateCommand(() =>
            {
                IsCameraActive = true;
            });

            Logger = App.Current.Container.Resolve<ILoggerFacade>();
            //ErrorsChanged += Validation_ErrorsChanged;

            Window.Current.Activated += Current_Activated;
        }

        /// <summary>
        /// Detects if the app loses focus and gets it again
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Current_Activated(object sender, WindowActivatedEventArgs e)
        {
            if (e.WindowActivationState == CoreWindowActivationState.Deactivated)
            {
                Logger.Log("Focus lost/Deactivated " + DateTime.Now, Category.Info, Priority.Low);
                // if the screenclip app is started, the focus of the application is lost
                if (_launchScreenClip)
                {
                    // now set the scan to true
                    _qrCodeScan = true;
                    Logger.Log("QR-code scan is now active", Category.Info, Priority.Low);
                    // user has switch the application to scan the QR-code
                    _launchScreenClip = false;
                }
            }
            else
            {
                Logger.Log("Focus/Activated " + DateTime.Now, Category.Info, Priority.Low);
                // if the app is focused again, check if a QR-Code is in the clipboard
                if (_qrCodeScan)
                {
                    ReadQRCodeFromClipboard();
                    _qrCodeScan = false;
                }
            }
        }

        /// <summary>
        /// Launch the MS screenclip app
        /// </summary>
        public async Task ScanQRCode()
        {
            bool result = await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-screenclip:edit?delayInSeconds=" + OpeningSeconds));
            if (result)
            {
                
                _launchScreenClip = true;
            }
        }

        private void OnTimedEvent(object sender, object e)
        {
            if (Seconds == 0)
            {
                _dispatcherTimer.Stop();
                _dispatcherTimer.Tick -= OnTimedEvent;
            }
            else
            {
                --Seconds;
            }
        }

        /// <summary>
        /// Read the image (QR-code) from the clipboard
        /// </summary>
        public async Task ReadQRCodeFromClipboard()
        {
            DataPackageView dataPackageView = Clipboard.GetContent();
            if (dataPackageView.Contains(StandardDataFormats.Bitmap))
            {
                IRandomAccessStreamReference imageReceived = null;
                try
                {
                    imageReceived = await dataPackageView.GetBitmapAsync();
                }
                catch (Exception ex)
                {
                    Logger.Log(ex.Message, Category.Exception, Priority.Medium);
                }
                finally
                {
                    try
                    {
                        if (imageReceived != null)
                        {
                            using IRandomAccessStreamWithContentType imageStream = await imageReceived.OpenReadAsync();
                            BitmapDecoder bitmapDecoder = await BitmapDecoder.CreateAsync(imageStream);
                            SoftwareBitmap softwareBitmap = await bitmapDecoder.GetSoftwareBitmapAsync();

                            string result = ReadQRCodeFromBitmap(softwareBitmap);
                            _qrCodeStr = HttpUtility.UrlDecode(result);
                            if (!string.IsNullOrEmpty(_qrCodeStr))
                            {
                                //clear the clipboard, if the image is read as TOTP
                                Clipboard.Clear();
                                ParseQRCode();
                                //move to the input dialog
                                SelectedPivotIndex = 1;

                                if (!string.IsNullOrEmpty(SecretKey)
                                   && !string.IsNullOrEmpty(Model.Issuer))  /*   && !string.IsNullOrEmpty(Model.Label)*/
                                {
                                    IsPrimaryBTNEnable = true;
                                }
                                else
                                {
                                    MessageDialog dialog = new MessageDialog(ResourceService.GetLocalizedString("AddAccountContentDialogQRCodeContentError"), Strings.Resources.Error);
                                    await dialog.ShowAsync();
                                    //move to the selection dialog
                                    SelectedPivotIndex = 0;
                                }
                            }
                            else
                            {
                                MessageDialog dialog = new MessageDialog(ResourceService.GetLocalizedString("AddAccountContentDialogQRCodeContentError"), Strings.Resources.Error);
                                await dialog.ShowAsync();
                            }
                        }
                        else
                        {
                            //TODO add error: empty Clipboard?
                        }
                    }
                    catch (Exception)
                    {
                        // TODO error by processing the image
                        throw;
                    }

                }
            }
        }

        /// <summary>
        /// Parses the QR code by splitting the different parts
        /// </summary>
        /// <returns>true if TOTP</returns>
        private bool ParseQRCode()
        {
            IProject2FAParser parser = App.Current.Container.Resolve<IProject2FAParser>();
            List<KeyValuePair<string, string>> valuePair = parser.ParseQRCodeStr(_qrCodeStr);
            if (valuePair.Count == 0)
            {
                return false;
            }

            Model = new TwoFACodeModel();
            foreach (KeyValuePair<string, string> item in valuePair)
            {
                switch (item.Key)
                {
                    case "secret":
                        SecretKey= item.Value;
                        break;
                    case "label":
                        Model.Label = item.Value;
                        RaisePropertyChanged(nameof(Label));
                        //RaisePropertyChanged(nameof(Label));
                        break;
                    case "issuer":
                        Model.Issuer = item.Value;
                        RaisePropertyChanged(nameof(Issuer));
                        //RaisePropertyChanged(nameof(Issuer));
                        break;
                    case "algorithm":
                        string algo = item.Value.ToLower();
                        switch (algo)
                        {
                            case "sha1":
                                Model.HashMode = OtpHashMode.Sha1;
                                break;
                            case "sha256":
                                Model.HashMode = OtpHashMode.Sha256;
                                break;
                            case "sha512":
                                Model.HashMode = OtpHashMode.Sha512;
                                break;
                            default:
                                break;
                        }
                        break;
                    case "period":
                        Model.Period = Convert.ToInt32(item.Value);
                        break;
                    case "digits":
                        Model.TotpSize = Convert.ToInt32(item.Value);
                        break;
                    default:
                        break;
                }
            }
            return true;
        }

        /// <summary>
        /// Checks if the inputs are correct and enables / disables the submit button
        /// </summary>
        private void CheckInputs()
        {
            IsPrimaryBTNEnable = !string.IsNullOrEmpty(SecretKey) && !string.IsNullOrEmpty(Label) && !string.IsNullOrEmpty(Issuer);
        }

        /// <summary>
        /// Read QR code from writeble bitmap  
        /// </summary>
        /// <param name="encodedStr"></param>
        /// <returns>decoded result</returns>
        private string ReadQRCodeFromBitmap(SoftwareBitmap bitmap)
        {
            try
            {
                QRCodeReader qrReader = new QRCodeReader();
                LuminanceSource luminance = new ZXing.SoftwareBitmapLuminanceSource(bitmap);
                BinaryBitmap bbmap = new BinaryBitmap(new HybridBinarizer(luminance));
                Result result = qrReader.decode(bbmap);
                return result == null ? string.Empty : result.Text;
            }
            catch (Exception ex)
            {
                Logger.Log(ex.Message, Category.Exception, Priority.Medium);
                TrackingManager.TrackException(ex);
                return string.Empty;
            }
        }

        //private void Validation_ErrorsChanged(object sender, DataErrorsChangedEventArgs e)
        //{
        //    OnPropertyChanged(nameof(Errors)); // Update Errors on every Error change, so I can bind to it.
        //}

        #region GetSet

        //public List<(string name, string message)> Errors
        //{
        //    get
        //    {
        //        var list = new List<(string name, string message)>();
        //        foreach (var item in from ValidationResult e in GetErrors(null) select e)
        //        {
        //            list.Add((item.MemberNames.FirstOrDefault(), item.ErrorMessage));
        //        }
        //        return list;
        //    }
        //}

        public TwoFACodeModel Model
        {
            get => _model;
            set => SetProperty(ref _model, value);
        }
        public int SelectedPivotIndex
        {
            get => _selectedPivotIndex;
            set => SetProperty(ref _selectedPivotIndex, value);
        }
        public bool IsPrimaryBTNEnable
        {
            get => _isButtonEnable;
            set => SetProperty(ref _isButtonEnable, value);
        }
        
        [Required]
        public string SecretKey
        {
            get => _secretKey;
            set
            {
                if (SetProperty(ref _secretKey, value))
                {
                    if (!string.IsNullOrEmpty(value))
                    {
                        // see https://tools.ietf.org/html/rfc4648 for Base32 specification
                        if (Regex.IsMatch(value, "^[A-Z2-7]*={0,6}$"))
                        {
                            Model.SecretByteArray = Base32Encoding.ToBytes(value);
                            CheckInputs();
                        }
                        else
                        {
#pragma warning disable CA2011 // Avoid infinite recursion
                            string secret = SecretKey.Replace("-", string.Empty);
                            secret = secret.Replace(" ", string.Empty);
                            SecretKey = secret.ToUpper();
#pragma warning restore CA2011 // Avoid infinite recursion
                        }
                    }
                }
            }
        }

        public string Issuer
        {
            get => Model.Issuer;
            set
            {
                Model.Issuer = value;
                CheckInputs();
            }
        }

        public string Label
        {
            get => Model.Label;
            set
            {
                Model.Label = value;
                CheckInputs();
            }
        }
        public bool ManualInput
        {
            get => _manualInput;
            set => SetProperty(ref _manualInput, value);
        }
        public int OpeningSeconds 
        {
            get => _openingSeconds;
            set => SetProperty(ref _openingSeconds, value);
        }
        public int Seconds 
        {
            get => _seconds;
            set => SetProperty(ref _seconds, value);
        }
        public bool IsCameraActive 
        {
            get => _isCameraActive;
            set => SetProperty(ref _isCameraActive, value);
        }
        #endregion
    }
}