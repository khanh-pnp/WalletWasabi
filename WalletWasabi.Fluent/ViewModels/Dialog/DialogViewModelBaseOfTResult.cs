using System;
using System.Threading.Tasks;

namespace WalletWasabi.Fluent.ViewModels.Dialog
{
	/// <summary>
	/// Base ViewModel class for Dialogs that returns a value back.
	/// </summary>
	/// <typeparam name="TResult">The type of the value to be returned when the dialog is finished.</typeparam>
	public abstract class DialogViewModelBase<TResult> : DialogViewModelBase
	{
		private TaskCompletionSource<TResult> CurrentTaskCompletionSource { get; set; }

		/// <summary>
		/// Method to be called when the dialog intends to close
		/// and ready to pass a value back to the caller.
		/// </summary>
		/// <param name="value">The return value of the dialog</param>
		public void CloseDialog(TResult value)
		{
			CurrentTaskCompletionSource.SetResult(value);
			CurrentTaskCompletionSource = null;

			CloseDialog();
		}

		/// <summary>
		/// Shows the dialog.
		/// </summary>
		/// <returns>The value to be returned when the dialog is finished.</returns>
		public Task<TResult> ShowDialogAsync(IDialogHost host)
		{
			host.CurrentDialog = this;

			if (CurrentTaskCompletionSource is { })
			{
				throw new InvalidOperationException("Can't open a new dialog since there's already one active.");
			}

			CurrentTaskCompletionSource = new TaskCompletionSource<TResult>();

			DialogShowing();

			IsDialogOpen = true;

			return CurrentTaskCompletionSource.Task;
		}
	}
}
