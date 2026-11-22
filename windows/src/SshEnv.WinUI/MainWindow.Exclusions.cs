using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using SshEnv.Core;
using Windows.System;

namespace SshEnv.WinUI;

public sealed partial class MainWindow
{
    private sealed class RuleListEditor
    {
        private readonly ListBox list = new() { Height = 260 };
        private readonly TextBox? address;
        private readonly AutoSuggestBox? search;

        public RuleListEditor(StackPanel panel, InfoBar feedback, bool applications)
        {
            AutomationProperties.SetName(list, L(applications ? "Applications" : "Addresses"));
            if (applications)
            {
                search = CreateApplicationSearch(feedback);
                panel.Children.Add(search);
            }
            else
            {
                address = new TextBox { PlaceholderText = "example.com, 10.0.0.0/8", MinHeight = 42, VerticalContentAlignment = VerticalAlignment.Center };
                AutomationProperties.SetName(address, L("Addresses"));
                panel.Children.Add(CreateAddressField(address, feedback));
            }
            panel.Children.Add(list);
        }

        private Grid CreateAddressField(TextBox input, InfoBar feedback)
        {
            void CommitInput()
            {
                Add(Pending());
                input.Text = "";
                input.Focus(FocusState.Programmatic);
            }

            var add = CreateDialogAction(L("AddRule"), feedback, () =>
            {
                CommitInput();
                return Task.CompletedTask;
            });
            input.KeyDown += (_, args) =>
            {
                if (args.Key != VirtualKey.Enter) return;
                args.Handled = true;
                try
                {
                    CommitInput();
                    feedback.IsOpen = false;
                    feedback.Visibility = Visibility.Collapsed;
                }
                catch (ArgumentException error)
                {
                    SetDialogError(feedback, error);
                }
            };
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(add, 1);
            row.Children.Add(input);
            row.Children.Add(add);
            return row;
        }

        private AutoSuggestBox CreateApplicationSearch(InfoBar feedback)
        {
            var box = new AutoSuggestBox
            {
                PlaceholderText = L("SearchApplications"),
                MinHeight = 42,
                UpdateTextOnSelect = false
            };
            AutomationProperties.SetName(box, L("SearchApplications"));
            IReadOnlyList<InstalledApplication> catalog = [];
            bool scanning = false;
            bool hasFocus = false;

            void UpdateSuggestions()
            {
                var query = box.Text.Trim();
                var matches = query.Length == 0 ? [] : catalog.Where(app =>
                    app.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                    app.ProcessName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    app.ExecutablePath.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
                box.ItemsSource = matches;
                box.IsSuggestionListOpen = matches.Length > 0 && hasFocus;
            }

            box.LostFocus += (_, _) => hasFocus = false;
            box.GotFocus += async (_, _) =>
            {
                hasFocus = true;
                if (scanning) return;
                scanning = true;
                try
                {
                    catalog = await Task.Run(InstalledApplicationCatalog.FindInstalledApplications);
                    UpdateSuggestions();
                }
                catch (Exception error)
                {
                    SetDialogError(feedback, error);
                }
                finally
                {
                    scanning = false;
                }
            };
            box.TextChanged += (_, args) =>
            {
                if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput) UpdateSuggestions();
            };
            box.QuerySubmitted += (_, args) =>
            {
                var name = (args.ChosenSuggestion as InstalledApplication)?.ProcessName;
                if (name is null)
                {
                    name = catalog.FirstOrDefault(app =>
                        SameName(app.DisplayName, args.QueryText.Trim()) || SameName(app.ProcessName, args.QueryText.Trim()))?.ProcessName;
                    if (name is null && args.QueryText.Trim().EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        name = args.QueryText.Trim();
                }
                if (name is null) return;

                try
                {
                    AddApplications([name]);
                    box.Text = "";
                    box.IsSuggestionListOpen = false;
                    feedback.IsOpen = false;
                    feedback.Visibility = Visibility.Collapsed;
                }
                catch (ArgumentException error)
                {
                    SetDialogError(feedback, error);
                }
            };
            return box;
        }

        private IEnumerable<SingBoxDirectRule> Items =>
            list.Items.Cast<ListBoxItem>().Select(item => (SingBoxDirectRule)item.Tag);

        private IReadOnlyList<SingBoxDirectRule> Pending()
        {
            var rules = SingBoxConfigDirectRules.Parse(address?.Text);
            if (rules.Any(rule => rule.Type is "process_name" or "preset"))
                throw new ArgumentException(L("AddressRulesOnly"));
            return rules;
        }

        public IEnumerable<SingBoxDirectRule> Read() => Items.Concat(Pending());

        public void Replace(IEnumerable<SingBoxDirectRule> rules)
        {
            list.Items.Clear();
            if (address is not null) address.Text = "";
            if (search is not null) search.Text = "";
            foreach (var rule in rules) Append(rule);
        }

        public void AddApplications(IEnumerable<string> names) =>
            Add(names.Select(name => new SingBoxDirectRule("process_name", name)));

        private void Add(IEnumerable<SingBoxDirectRule> additions)
        {
            var merged = SingBoxConfigDirectRules.Normalize(Items.Concat(additions));
            var existing = Items.ToHashSet();
            foreach (var obsolete in list.Items.Cast<ListBoxItem>().Where(item => !merged.Contains((SingBoxDirectRule)item.Tag)).ToArray())
                list.Items.Remove(obsolete);
            foreach (var rule in merged.Where(rule => !existing.Contains(rule))) Append(rule);
        }

        private void Append(SingBoxDirectRule rule)
        {
            var name = rule.Type == "process_name" ? rule.Value : SingBoxConfigDirectRules.Format(rule);
            var label = new TextBlock { Text = name, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            var remove = new Button { Content = L("Delete") };
            var actionName = $"{L("Delete")} {name}";
            AutomationProperties.SetName(remove, actionName);
            ToolTipService.SetToolTip(remove, actionName);
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(remove, 1);
            row.Children.Add(label);
            row.Children.Add(remove);
            var item = new ListBoxItem { Content = row, Tag = rule, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            remove.Click += (_, _) => list.Items.Remove(item);
            list.Items.Add(item);
        }
    }

    private static Expander CreatePresetPreview(string id, InfoBar feedback)
    {
        var filter = new TextBox { PlaceholderText = L("FilterRules") };
        AutomationProperties.SetName(filter, L("FilterRules"));
        var list = new ListView { Height = 200 };
        AutomationProperties.SetName(list, L($"ExclusionPreset_{id}"));
        var status = new TextBlock();
        var preview = new Expander
        {
            Header = L("PresetPreview"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new StackPanel { Spacing = 6, Children = { filter, status, list } }
        };
        IReadOnlyList<string>? entries = null;
        void Filter()
        {
            if (entries is null) return;
            var query = filter.Text.Trim();
            var matches = query.Length == 0 ? entries : entries.Where(entry => entry.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
            list.ItemsSource = matches;
            status.Text = matches.Count == 0 ? L("NoRuleMatches") : $"{matches.Count:N0} / {entries.Count:N0}";
        }

        preview.Expanding += (_, _) =>
        {
            try
            {
                entries ??= SingBoxConfigDirectRules.GetPresetEntries(id);
                Filter();
            }
            catch (Exception error)
            {
                SetDialogError(feedback, error);
            }
        };
        filter.TextChanged += (_, _) => Filter();
        return preview;
    }
}
