using TaleWorlds.Library;

namespace CK3Bridge
{
    /// <summary>
    /// View model of the CK3 title card. One shape serves the loading card (GUI/Prefabs/CK3LoadingWindow.xml), the
    /// in-engine title card and result footer (CK3TitleCard.xml) and the black backdrop (CK3Backdrop.xml, no bindings);
    /// both card prefabs embed CK3CardBlock.xml, so the loading card and the title card have the identical layout and the
    /// switch between them is invisible.
    /// </summary>
    public sealed class CK3CardVM : ViewModel
    {
        private bool _isActive;
        private bool _isCardVisible;
        private string _title = "";
        private string _subtitle = "";
        private string _detail = "";
        private bool _hasSubtitle;
        private bool _hasDetail;
        private bool _hasCoats;
        private string _playerCoaProvider = "";
        private string _enemyCoaProvider = "";
        private string _footer = "";
        private bool _isFooterVisible;
        private CardTexts _shown;

        /// <summary>
        /// Shows <paramref name="texts"/> (null hides the card block) with the battle's coats of arms. Texts are built
        /// once per battle and the coats are loaded before them, so an unchanged reference means nothing to update.
        /// </summary>
        public void SetCard(CardTexts texts)
        {
            if (ReferenceEquals(texts, _shown)) return;
            _shown = texts;
            IsCardVisible = texts != null;
            Title = texts?.Title ?? "";
            Subtitle = texts?.Subtitle ?? "";
            Detail = texts?.Detail ?? "";
            HasSubtitle = Subtitle.Length > 0;
            HasDetail = Detail.Length > 0;
            // An empty provider name keeps TextureWidget from creating a provider at all (TextureWidget.UpdateTextureWidget).
            PlayerCoaProvider = texts != null && CoatOfArmsTextures.Player != null ? nameof(CK3PlayerCoaTextureProvider) : "";
            EnemyCoaProvider = texts != null && CoatOfArmsTextures.Enemy != null ? nameof(CK3EnemyCoaTextureProvider) : "";
            HasCoats = texts != null && CoatOfArmsTextures.Any;
        }

        public void ShowFooter(string text)
        {
            Footer = text ?? "";
            IsFooterVisible = Footer.Length > 0;
        }

        public void HideFooter() => IsFooterVisible = false;

        /// <summary>Loading card only: whether the loading window is enabled.</summary>
        [DataSourceProperty]
        public bool IsActive
        {
            get => _isActive;
            set
            {
                if (value == _isActive) return;
                _isActive = value;
                OnPropertyChangedWithValue(value, nameof(IsActive));
            }
        }

        [DataSourceProperty]
        public bool IsCardVisible
        {
            get => _isCardVisible;
            set
            {
                if (value == _isCardVisible) return;
                _isCardVisible = value;
                OnPropertyChangedWithValue(value, nameof(IsCardVisible));
            }
        }

        [DataSourceProperty]
        public string Title
        {
            get => _title;
            set
            {
                if (value == _title) return;
                _title = value;
                OnPropertyChangedWithValue(value, nameof(Title));
            }
        }

        [DataSourceProperty]
        public string Subtitle
        {
            get => _subtitle;
            set
            {
                if (value == _subtitle) return;
                _subtitle = value;
                OnPropertyChangedWithValue(value, nameof(Subtitle));
            }
        }

        [DataSourceProperty]
        public string Detail
        {
            get => _detail;
            set
            {
                if (value == _detail) return;
                _detail = value;
                OnPropertyChangedWithValue(value, nameof(Detail));
            }
        }

        [DataSourceProperty]
        public bool HasSubtitle
        {
            get => _hasSubtitle;
            set
            {
                if (value == _hasSubtitle) return;
                _hasSubtitle = value;
                OnPropertyChangedWithValue(value, nameof(HasSubtitle));
            }
        }

        [DataSourceProperty]
        public bool HasDetail
        {
            get => _hasDetail;
            set
            {
                if (value == _hasDetail) return;
                _hasDetail = value;
                OnPropertyChangedWithValue(value, nameof(HasDetail));
            }
        }

        /// <summary>Both coat-of-arms slots are shown when either coat exists, so the text block stays centred.</summary>
        [DataSourceProperty]
        public bool HasCoats
        {
            get => _hasCoats;
            set
            {
                if (value == _hasCoats) return;
                _hasCoats = value;
                OnPropertyChangedWithValue(value, nameof(HasCoats));
            }
        }

        [DataSourceProperty]
        public string PlayerCoaProvider
        {
            get => _playerCoaProvider;
            set
            {
                if (value == _playerCoaProvider) return;
                _playerCoaProvider = value;
                OnPropertyChangedWithValue(value, nameof(PlayerCoaProvider));
            }
        }

        [DataSourceProperty]
        public string EnemyCoaProvider
        {
            get => _enemyCoaProvider;
            set
            {
                if (value == _enemyCoaProvider) return;
                _enemyCoaProvider = value;
                OnPropertyChangedWithValue(value, nameof(EnemyCoaProvider));
            }
        }

        [DataSourceProperty]
        public string Footer
        {
            get => _footer;
            set
            {
                if (value == _footer) return;
                _footer = value;
                OnPropertyChangedWithValue(value, nameof(Footer));
            }
        }

        [DataSourceProperty]
        public bool IsFooterVisible
        {
            get => _isFooterVisible;
            set
            {
                if (value == _isFooterVisible) return;
                _isFooterVisible = value;
                OnPropertyChangedWithValue(value, nameof(IsFooterVisible));
            }
        }
    }
}
