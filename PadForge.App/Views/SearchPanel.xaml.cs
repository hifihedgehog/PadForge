using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PadForge.Search;
using PadForge.ViewModels;

namespace PadForge.Views
{
    /// <summary>Search (#574): the query, the grouped results and the key
    /// hints. Up and Down move the pick, Page Up and Page Down move it five,
    /// Enter opens it, and Ctrl+K selects the query's words. The ShellDialog
    /// that hosts it closes it on Escape.</summary>
    public partial class SearchPanel : UserControl
    {
        public SearchPanel()
        {
            InitializeComponent();
        }

        private SearchViewModel Vm => DataContext as SearchViewModel;

        private void Panel_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var vm = Vm;
            if (vm == null) return;
            int move = e.Key switch
            {
                Key.Up => -1,
                Key.Down => 1,
                Key.PageUp => -5,
                Key.PageDown => 5,
                _ => 0,
            };
            if (move != 0 && Keyboard.Modifiers == ModifierKeys.None)
            {
                vm.MoveSelection(move);
                if (vm.SelectedResult != null) SearchResultsList.ScrollIntoView(vm.SelectedResult);
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None
                && !ReferenceEquals(Keyboard.FocusedElement, SearchCloseChip))
            {
                // Enter on the close chip is left to the chip, which closes.
                vm.Open();
                e.Handled = true;
            }
            else if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control)
            {
                FocusQuery();
                e.Handled = true;
            }
        }

        private void Result_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is ListBoxItem { DataContext: SearchResult result }) Vm?.Open(result);
        }

        private void CloseChip_Click(object sender, RoutedEventArgs e) => Vm?.Close();

        /// <summary>Puts the focus in the query box with its words
        /// selected, so typing replaces them and Enter repeats them.</summary>
        public void FocusQuery()
        {
            SearchQueryBox.Focus();
            SearchQueryBox.SelectAll();
        }

        /// <summary>Holds the results to the page area's height: what the
        /// window leaves under the dialog's 40 above and below, less the
        /// query row and the foot.</summary>
        public void FitTo(double areaHeight)
        {
            double room = areaHeight - 80 - QueryRow.ActualHeight - SearchFoot.ActualHeight - 2;
            SearchResultsList.MaxHeight = Math.Max(96, room);
        }
    }
}
