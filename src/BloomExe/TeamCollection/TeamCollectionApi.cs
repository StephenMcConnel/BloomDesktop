using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Bloom.Api;
using Bloom.Book;
using Bloom.Collection;
using Bloom.MiscUI;
using L10NSharp;
using Newtonsoft.Json;
using SIL.Reporting;

namespace Bloom.TeamCollection
{
	// Implements functions used by the HTML/Typescript parts of the Team Collection code.
	// Review: should this be in web/controllers with all the other API classes, or here with all the other sharing code?
	public class TeamCollectionApi
	{
		private TeamCollectionManager _tcManager;
		private BookSelection _bookSelection; // configured by autofac, tells us what book is selected
		private BookServer _bookServer;
		private string CurrentUser => TeamCollectionManager.CurrentUser;
		private string _folderForCreateTC;
		private BloomWebSocketServer _socketServer;

		public static TeamCollectionApi TheOneInstance { get; private set; }

		// Called by autofac, which creates the one instance and registers it with the server.
		public TeamCollectionApi(CollectionSettings settings, BookSelection bookSelection, TeamCollectionManager tcManager, BookServer bookServer, BloomWebSocketServer socketServer)
		{
			_tcManager = tcManager;
			_tcManager.CurrentCollection?.SetupMonitoringBehavior();
			_bookSelection = bookSelection;
			_socketServer = socketServer;
			_bookServer = bookServer;
			TheOneInstance = this;
		}

		public void RegisterWithApiHandler(BloomApiHandler apiHandler)
		{
			apiHandler.RegisterEndpointHandler("teamCollection/isTeamCollectionEnabled", HandleIsTeamCollectionEnabled, false);
			apiHandler.RegisterEndpointHandler("teamCollection/currentBookStatus", HandleCurrentBookStatus, false);
			apiHandler.RegisterEndpointHandler("teamCollection/attemptLockOfCurrentBook", HandleAttemptLockOfCurrentBook, false);
			apiHandler.RegisterEndpointHandler("teamCollection/checkInCurrentBook", HandleCheckInCurrentBook, true);
			apiHandler.RegisterEndpointHandler("teamCollection/chooseFolderLocation", HandleChooseFolderLocation, true);
			apiHandler.RegisterEndpointHandler("teamCollection/createTeamCollection", HandleCreateTeamCollection, true);
			apiHandler.RegisterEndpointHandler("teamCollection/joinTeamCollection", HandleJoinTeamCollection, true);
		}

		private void HandleJoinTeamCollection(ApiRequest request)
		{
			FolderTeamCollection.JoinCollectionTeam();
			BrowserDialog.CloseDialog();
			request.PostSucceeded();
		}

		public void HandleIsTeamCollectionEnabled(ApiRequest request)
		{
			// We don't need any of the Sharing UI if the selected book isn't in the editable
			// collection (or if the collection doesn't have a Team Collection at all).
			request.ReplyWithBoolean(_tcManager.CurrentCollection != null &&
				(_bookSelection.CurrentSelection == null || _bookSelection.CurrentSelection.IsEditable));
		}

		public void HandleCurrentBookStatus(ApiRequest request)
		{
			var whoHasBookLocked = _tcManager.CurrentCollection?.WhoHasBookLocked(BookFolderName);
			var whenLocked = _tcManager.CurrentCollection?.WhenWasBookLocked(BookFolderName) ?? DateTime.MaxValue;
			// review: or better to pass on to JS? We may want to show slightly different
			// text like "This book is not yet shared. Check it in to make it part of the team collection"
			if (whoHasBookLocked == TeamCollection.FakeUserIndicatingNewBook)
				whoHasBookLocked = CurrentUser;
			request.ReplyWithJson(JsonConvert.SerializeObject(
				new
				{
					who = whoHasBookLocked,
					whoFirstName = _tcManager.CurrentCollection?.WhoHasBookLockedFirstName(BookFolderName),
					whoSurname = _tcManager.CurrentCollection?.WhoHasBookLockedSurname(BookFolderName),
					when = whenLocked.ToLocalTime().ToShortDateString(),
					where = _tcManager.CurrentCollection?.WhatComputerHasBookLocked(BookFolderName),
					currentUser = CurrentUser,
					currentMachine = TeamCollectionManager.CurrentMachine
				}));
		}

		private string BookFolderName => Path.GetFileNameWithoutExtension(_bookSelection.CurrentSelection?.FolderPath);

		public void HandleAttemptLockOfCurrentBook(ApiRequest request)
		{
			// Could be a problem if there's no current book or it's not in the collection folder.
			// But in that case, we don't show the UI that leads to this being called.
			var success = _tcManager.CurrentCollection.AttemptLock(BookFolderName);
			if (success)
				UpdateUiForBook();
			request.ReplyWithBoolean(success);
		}

		public void HandleCheckInCurrentBook(ApiRequest request)
		{
			_bookSelection.CurrentSelection.Save();
			var bookName = Path.GetFileName(_bookSelection.CurrentSelection.FolderPath);
			if (_tcManager.CurrentCollection.OkToCheckIn(bookName))
			{
				_tcManager.CurrentCollection.PutBook(_bookSelection.CurrentSelection.FolderPath, true);
			}
			else
			{
				// We can't check in! The system has broken down...perhaps conflicting checkouts while offline.
				// Save our version in Lost-and-Found
				_tcManager.CurrentCollection.PutBook(_bookSelection.CurrentSelection.FolderPath, false,true);
				// overwrite it with the current repo version.
				_tcManager.CurrentCollection.CopyBookFromRepoToLocal(bookName);
				// Force a full reload of the book from disk and update the UI to match.
				_bookSelection.SelectBook(_bookServer.GetBookFromBookInfo(_bookSelection.CurrentSelection.BookInfo, true));
				var msg = LocalizationManager.GetString("TeamCollection.ConflictingEditOrCheckout",
					"Someone else has edited this book or checked it out even though you were editing it! Your changes have been saved to Lost and Found");
				ErrorReport.NotifyUserOfProblem(msg);
			}

			UpdateUiForBook();
			request.PostSucceeded();
		}

		// This supports sending a notification to the CollectionSettings dialog when the Create link is used
		// to connect to a newly created shared folder and make this collection a Team Collection.
		private Action _createCallback;

		public void SetCreateCallback(Action callback)
		{
			_createCallback = callback;
		}

		public void HandleChooseFolderLocation(ApiRequest request)
		{
			// One of the few places that knows we're using a particular implementation
			// of TeamRepo. But we have to know that to create it. And of course the user
			// has to chose a folder to get things started.
			// We'll need a different API or something similar if we ever want to create
			// some other kind of repo.
			using (var dlg = new FolderBrowserDialog())
			{
				dlg.ShowNewFolderButton = true;
				dlg.Description = LocalizationManager.GetString("TeamCollection.SelectFolder",
					"Select or create the folder where this collection will be shared");
				if (DialogResult.OK != dlg.ShowDialog())
				{
					request.Failed();
					return;
				}

				_folderForCreateTC = dlg.SelectedPath;
			}
			// We send the result through a websocket rather than simply returning it because
			// if the user is very slow (one site said FF times out after 90s) the browser may
			// abandon the request before it completes. The POST result is ignored and the
			// browser simply listens to the socket.
			// We'd prefer this request to return immediately and set a callback to run
			// when the dialog closes and handle the results, but FolderBrowserDialog
			// does not offer such an API. Instead, we just ignore any timeout
			// in our Javascript code.
			dynamic messageBundle = new DynamicJson();
			messageBundle.repoFolderPath = _folderForCreateTC;
			messageBundle.problem = ProblemsWithLocation(_folderForCreateTC);
			// This clientContext must match what is being listened for in CreateTeamCollection.tsx
			_socketServer.SendBundle("teamCollectionCreate", "shared-folder-path", messageBundle);

			request.PostSucceeded();
		}

		private string ProblemsWithLocation(string sharedFolder)
		{
			// For now we use this generic message, because it's too hard to come up with concise
			// understandable messages explaining why these locations are a problem.
			var defaultMessage = LocalizationManager.GetString("TeamCollection.ProblemLocation",
				"There is a problem with this location");
			try
			{
				if (Directory.EnumerateFiles(sharedFolder, "*.JoinBloomTC").Any())
				{
					return defaultMessage;
					//return LocalizationManager.GetString("TeamCollection.AlreadyTC",
					//	"This folder appears to already be in use as a Team Collection");
				}

				if (Directory.EnumerateFiles(sharedFolder, "*.bloomCollection").Any())
				{
					return defaultMessage;
					//return LocalizationManager.GetString("TeamCollection.LocalCollection",
					//	"This appears to be a local Bloom collection. The Team Collection must be created in a distinct place.");
				}

				if (Directory.Exists(_tcManager.PlannedRepoFolderPath(sharedFolder)))
				{
					return defaultMessage;
					//return LocalizationManager.GetString("TeamCollection.TCExists",
					//	"There is already a Folder in that location with the same name as this collection");
				}

				// We're not in a big hurry here, and the most decisive test that we can actually put things in this
				// folder is to do it.
				var testFolder = Path.Combine(sharedFolder, "test");
				Directory.CreateDirectory(testFolder);
				File.WriteAllText(Path.Combine(testFolder, "test"), "This is a test");
				SIL.IO.RobustIO.DeleteDirectoryAndContents(testFolder);
			}
			catch (Exception ex)
			{
				// This might also catch errors such as not having permission to enumerate things
				// in the directory.
				return LocalizationManager.GetString("TeamCollection.NoWriteAccess",
					"Bloom does not have permission to write to the selected folder. The system reported " +
					ex.Message);
			}

			return "";
		}

		public void HandleCreateTeamCollection(ApiRequest request)
		{
			_tcManager.ConnectToTeamCollection(_folderForCreateTC);
			BrowserDialog.CloseDialog();
			_createCallback?.Invoke();

			request.PostSucceeded();
		}


		// Called when we cause the book's status to change, so things outside the HTML world, like visibility of the
		// "Edit this book" button, can change appropriately. Pretending the user chose a different book seems to
		// do all the necessary stuff for now.
		private void UpdateUiForBook()
		{
			// Todo: This is not how we want to do this. Probably the UI should listen for changes to the status of books,
			// whether selected or not, talking to the repo directly.
			Form.ActiveForm.Invoke((Action) (() => _bookSelection.InvokeSelectionChanged(false)));
		}

		// Some pre-existing logic for whether the user can edit the book, combined with checking
		// that it is checked-out to this user 
		public bool CanEditBook()
		{
			var folderName = BookFolderName;
			if (string.IsNullOrEmpty(folderName) || !_bookSelection.CurrentSelection.IsEditable || _bookSelection.CurrentSelection.HasFatalError)
			{
				return false; // no book, no editing
			}
			if (_tcManager.CurrentCollection == null)
			{
				return true; // no team collection, no problem.
			}

			return _tcManager.CurrentCollection.IsCheckedOutHereBy(_tcManager.CurrentCollection.GetStatus(folderName));
		}
	}
}