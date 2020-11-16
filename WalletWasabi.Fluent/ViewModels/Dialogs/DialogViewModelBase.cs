using System.Windows.Input;
using ReactiveUI;
using WalletWasabi.Gui.ViewModels;

namespace WalletWasabi.Fluent.ViewModels.Dialogs
{
	/// <summary>
	/// CommonBase class.
	/// </summary>
	public abstract class DialogViewModelBase : RoutableViewModel
	{
		private bool _isDialogOpen;

		public DialogViewModelBase(NavigationStateViewModel navigationState, NavigationTarget navigationTarget) : base(navigationState, navigationTarget)
		{
		}

		public abstract void Close();

		public ICommand NextCommand { get; protected set; }

		/// <summary>
		/// Gets or sets if the dialog is opened/closed.
		/// </summary>
		public bool IsDialogOpen
		{
			get => _isDialogOpen;
			set => this.RaiseAndSetIfChanged(ref _isDialogOpen, value);
		}
	}
}