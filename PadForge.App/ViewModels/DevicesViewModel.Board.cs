using System;
using System.Collections;
using System.Windows.Data;
using System.Windows.Media;

namespace PadForge.ViewModels
{
    /// <summary>
    /// What the Devices page's table and panel read of the page since the
    /// world-class pass (#574): the parts of the connected list in their
    /// order, which of the table's columns have anything in them, and the
    /// selected device's picture.
    /// </summary>
    public partial class DevicesViewModel
    {
        /// <summary>The connected list's parts stand in the Show segments'
        /// order, whichever part a device first turned up in.</summary>
        internal sealed class SectionOrder : IComparer
        {
            internal static readonly SectionOrder Instance = new();

            public int Compare(object x, object y) => Rank(x).CompareTo(Rank(y));

            private static int Rank(object group)
                => group is CollectionViewGroup { Name: DeviceFilter section } ? (int)section : int.MaxValue;
        }

        private bool _showLinkColumn, _showBatteryColumn;

        /// <summary>Whether any row the lists show names its link. The
        /// table's head has no Link while none does, and the column then
        /// takes no room.</summary>
        public bool ShowLinkColumn
        {
            get => _showLinkColumn;
            private set => SetProperty(ref _showLinkColumn, value);
        }

        /// <summary>Whether any row the lists show reports a battery.</summary>
        public bool ShowBatteryColumn
        {
            get => _showBatteryColumn;
            private set => SetProperty(ref _showBatteryColumn, value);
        }

        /// <summary>Reads the two columns again. It runs with the counts,
        /// and after the battery walk, which changes no count.</summary>
        public void RefreshColumns()
        {
            bool link = false, battery = false;
            foreach (var d in Devices)
            {
                if (d == null || !Matches(d)) continue;
                link |= d.HasLink;
                battery |= d.HasBattery;
            }
            ShowLinkColumn = link;
            ShowBatteryColumn = battery;
        }

        private ImageSource _selectedPicture, _selectedPictureGray;
        private bool _pictureMade, _pictureStill;
        private Guid _pictureDevice;
        private ushort _pictureVendor, _pictureProduct;

        /// <summary>The selected device's picture at the head of its panel,
        /// in its own colors: the controller it is, where PadForge has that
        /// controller's art. Null where it has none, and the panel shows
        /// the device's kind.</summary>
        public ImageSource SelectedPicture => _selectedPicture;

        /// <summary>The same picture in gray, for a device that is not
        /// connected.</summary>
        public ImageSource SelectedPictureGray => _selectedPictureGray;

        /// <summary>Whether the panel has a picture to show. False while a
        /// still of the 3D model is being made, when the stage shows
        /// nothing for that moment.</summary>
        public bool HasSelectedPicture => _selectedPicture != null;

        /// <summary>Whether the stage shows the device's kind: it has no
        /// picture and none is on its way.</summary>
        public bool ShowSelectedKind => _selectedPicture == null && !_pictureComing;

        private bool _pictureComing;

        /// <summary>Whether the picture shown was made for this device, these
        /// ids and this view (a still of the 3D model, or the 2D art).</summary>
        internal bool PictureIsFor(Guid device, ushort vendorId, ushort productId, bool still)
            => _pictureMade && _pictureDevice == device && _pictureVendor == vendorId && _pictureProduct == productId
                && _pictureStill == still;

        /// <summary>Sets the picture and what it was made for.
        /// <paramref name="coming"/> says a still is being made and no
        /// picture is here yet.</summary>
        internal void SetPicture(Guid device, ushort vendorId, ushort productId, bool still,
            ImageSource picture, ImageSource gray, bool coming = false)
        {
            _pictureMade = true;
            _pictureDevice = device;
            _pictureVendor = vendorId;
            _pictureProduct = productId;
            _pictureStill = still;
            bool changed = !ReferenceEquals(_selectedPicture, picture) || !ReferenceEquals(_selectedPictureGray, gray)
                || _pictureComing != coming;
            _selectedPicture = picture;
            _selectedPictureGray = gray;
            _pictureComing = coming;
            if (!changed) return;
            OnPropertyChanged(nameof(SelectedPicture));
            OnPropertyChanged(nameof(SelectedPictureGray));
            OnPropertyChanged(nameof(HasSelectedPicture));
            OnPropertyChanged(nameof(ShowSelectedKind));
        }

        /// <summary>The picture is asked for again at the next look: a
        /// still that was being made is ready.</summary>
        internal void ForgetPicture() => _pictureMade = false;
    }
}
