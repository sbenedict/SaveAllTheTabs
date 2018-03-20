using System;
using System.Windows;

namespace SaveAllTheTabs
{
    /// <summary>
    /// Interaction logic for ImportGroupsConfirmOverwriteWindow.xaml
    /// </summary>
    public partial class ImportGroupsConfirmOverwriteWindow : Window
    {
        public ImportGroupsConfirmOverwriteWindow()
        {
            InitializeComponent();
        }

        private void OnConfirm(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
