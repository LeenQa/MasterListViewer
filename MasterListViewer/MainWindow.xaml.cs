// 
// Copyright (c) 2026 LeenQa. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
//

using ClosedXML.Excel;
using Microsoft.Win32;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.X509;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;

namespace MasterListViewer
{
    public partial class MainWindow : Window
    {
        public ObservableCollection<CertRow> Certificates { get; } = new();
        public ObservableCollection<CountryCount> CountryCounts { get; } = new();

        public ICollectionView CertificatesView { get; }

        public int TotalCount => CertificatesView?.Cast<object>().Count() ?? 0;
        public string CurrentFile { get; private set; } = string.Empty;

        public MainWindow()
        {
            InitializeComponent();
            CertificatesView = CollectionViewSource.GetDefaultView(Certificates);
            CertificatesView.Filter = CertFilter;
            DataContext = this;
        }

        private bool _showOnlyUnexpired;
        public bool ShowOnlyUnexpired
        {
            get => _showOnlyUnexpired;
            set
            {
                if (_showOnlyUnexpired == value) return;
                _showOnlyUnexpired = value;
                OnPropertyChanged(nameof(ShowOnlyUnexpired));
                CertificatesView.Refresh();
                RebuildCountryCountsFromFiltered();
                OnPropertyChanged(nameof(TotalCount));
            }
        }

        private bool CertFilter(object obj)
        {
            if (obj is not CertRow row) return false;
            if (!ShowOnlyUnexpired) return true;

            return row.NotAfterUtc >= DateTime.UtcNow;
        }

        private void BtnOpen_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Open ICAO Master List (.ml)",
                Filter = "Master List (*.ml)|*.ml|All files (*.*)|*.*"
            };

            if (dlg.ShowDialog(this) == true)
            {
                try
                {
                    LoadMasterList(dlg.FileName);
                    CurrentFile = Path.GetFileName(dlg.FileName);
                    OnPropsChanged(nameof(CurrentFile));
                    OnPropsChanged(nameof(TotalCount));
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, $"Failed to parse Master List:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
        private void BtnExportCountryCounts_Click(object sender, RoutedEventArgs e)
        {
            if (CountryCounts == null || CountryCounts.Count == 0)
            {
                MessageBox.Show(this, "No data to export. Load a Master List first.", "Nothing to Export",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new SaveFileDialog
            {
                Title = "Export Country Counts",
                Filter = "Excel Workbook (*.xlsx)|*.xlsx",
                FileName = $"CountryCounts_{DateTime.Now:yyyyMMdd_HHmm}.xlsx"
            };

            if (dlg.ShowDialog(this) != true) return;

            try
            {
                using var wb = new XLWorkbook();
                var ws = wb.AddWorksheet("Country Counts");

                ws.Cell(1, 1).Value = "Country";
                ws.Cell(1, 2).Value = "Count";
                ws.Row(1).Style.Font.Bold = true;

                for (int i = 0; i < CountryCounts.Count; i++)
                {
                    ws.Cell(i + 2, 1).Value = CountryCounts[i].Country;
                    ws.Cell(i + 2, 2).Value = CountryCounts[i].Count;
                }

                int lastRow = CountryCounts.Count + 1;
                ws.Cell(lastRow + 1, 1).Value = "Total";
                ws.Cell(lastRow + 1, 2).FormulaA1 = $"SUM(B2:B{lastRow})";
                ws.Row(lastRow + 1).Style.Font.Bold = true;

                ws.Range(1, 1, lastRow, 2).SetAutoFilter();
                ws.Columns().AdjustToContents();

                wb.SaveAs(dlg.FileName);

                MessageBox.Show(this, "Export completed successfully.", "Export",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Export failed:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        
        private void LoadMasterList(string filePath)
        {
            Certificates.Clear();
            CountryCounts.Clear();

            var data = File.ReadAllBytes(filePath);
            var allCerts = MasterListExtractor.ExtractAllCertificatesFromMasterList(data);

            int idx = 1;
            foreach (var cert in allCerts)
            {
                var subject = cert.SubjectDN;
                var issuer = cert.IssuerDN;

                string country = TryGetAttr(subject, X509Name.C)
                                 ?? TryGetAttr(issuer, X509Name.C)
                                 ?? string.Empty;

                Certificates.Add(new CertRow
                {
                    Index = idx++,
                    Country = string.IsNullOrWhiteSpace(country) ? "(Unknown)" : CountryCodeMapper.GetFullName(country),
                    SubjectCN = TryGetAttr(subject, X509Name.CN) ?? subject.ToString(),
                    IssuerCN = TryGetAttr(issuer, X509Name.CN) ?? issuer.ToString(),
                    SerialNumber = cert.SerialNumber.ToString(16).ToUpperInvariant(),
                    NotBefore = cert.NotBefore.ToString("yyyy-MM-dd"),
                    NotAfter = cert.NotAfter.ToString("yyyy-MM-dd"),
                    NotBeforeUtc = cert.NotBefore.ToUniversalTime(),
                    NotAfterUtc = cert.NotAfter.ToUniversalTime(),
                });
            }

            CurrentFile = System.IO.Path.GetFileName(filePath);
            OnPropertyChanged(nameof(CurrentFile));

            CertificatesView.Refresh();
            RebuildCountryCountsFromFiltered();
            OnPropertyChanged(nameof(TotalCount));
        }

        private void RebuildCountryCountsFromFiltered()
        {
            CountryCounts.Clear();

            var filtered = CertificatesView.Cast<CertRow>();
            foreach (var grp in filtered
                     .GroupBy(c => string.IsNullOrWhiteSpace(c.Country) ? "(Unknown)" : c.Country)
                     .OrderBy(g => g.Key))
            {
                CountryCounts.Add(new CountryCount { Country = grp.Key, Count = grp.Count() });
            }

            OnPropertyChanged(nameof(TotalCount));
        }


        private static string? TryGetAttr(X509Name name, DerObjectIdentifier oid)
        {
            try
            {
                var values = name.GetValueList(oid);
                if (values != null && values.Count > 0)
                    return values[0]?.ToString();
            }
            catch { }
            return null;
        }

        private void OnPropsChanged(params string[] names)
        {
            foreach (var n in names)
                this.Dispatcher.Invoke(() =>
                {
                    var dc = DataContext;
                    DataContext = null;
                    DataContext = dc;
                });
        }
    }

    public sealed class CertRow
    {
        public int Index { get; set; }
        public string Country { get; set; } = "";
        public string SubjectCN { get; set; } = "";
        public string IssuerCN { get; set; } = "";
        public string SerialNumber { get; set; } = "";
        public string NotBefore { get; set; } = "";
        public string NotAfter { get; set; } = "";
        public DateTime NotBeforeUtc { get; set; }
        public DateTime NotAfterUtc { get; set; }
    }

    public sealed class CountryCount
    {
        public string Country { get; set; } = "";
        public int Count { get; set; }
    }
}
