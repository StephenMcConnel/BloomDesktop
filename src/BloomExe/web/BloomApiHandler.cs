﻿// Copyright (c) 2018 SIL International
// This software is licensed under the MIT License (http://opensource.org/licenses/MIT)
//
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using SIL.IO;

using Bloom.Book;
using Bloom.ImageProcessing;
using Bloom.Collection;
using System.Threading;

namespace Bloom.Api
{
	/// <summary>
	/// This class handles the API (non-file) requests to the Bloom localhost HTTP server.
	/// Most of the API requests are handled by separate classes that register API calls
	/// with this class.  There are a couple of older API requests that are handled directly
	/// in this class's ProcessRequest method (i18n/ and directoryWatcher/).
	///
	/// This class also handles thread synchronization for those API requests that need it.
	/// </summary>
	public class BloomApiHandler
	{
		// This dictionary ties API endpoints to functions that handle the requests.
		private Dictionary<string, EndpointRegistration> _endpointRegistrations = new Dictionary<string, EndpointRegistration>();
		// Special lock for making thumbnails. See discussion at the one point of usage.
		private object ThumbnailsAndPreviewsSyncObj = new object();
		// We use two different locks to synchronize access to API requests.
		// This allows certain methods to run concurrently.
		private object I18NLock = new object();		// used to synchronize access to I18N methods
		private object SyncObj = new object();		// used to synchronize access to various other methods
		private readonly BookSelection _bookSelection;

		public CollectionSettings CurrentCollectionSettings { get; private set; }

		public BloomApiHandler(BookSelection bookSelection, CollectionSettings collectionSettings)
		{
			_bookSelection = bookSelection;
			CurrentCollectionSettings = collectionSettings;
		}

		/// <summary>
		/// Get called when a client (i.e. javascript) does an HTTP api call
		/// </summary>
		/// <param name="pattern">Simple string or regex to match APIs that this can handle. This must match what comes after the ".../api/" of the URL</param>
		/// <param name="handler">The method to call</param>
		/// <param name="handleOnUiThread">If true, the current thread will suspend until the UI thread can be used to call the method.
		/// This deliberately no longer has a default. It's something that should be thought about.
		/// Making it true can kill performance if you don't need it (BL-3452), and complicates exception handling and problem reporting (BL-4679).
		/// There's also danger of deadlock if something in the UI thread is somehow waiting for this request to complete.
		/// But, beware of race conditions or anything that manipulates UI controls if you make it false.</param>
		/// <param name="requiresSync">True if the handler wants the server to ensure no other thread is doing an api
		/// call while this one is running. This is our default behavior, ensuring that no API request can interfere with any
		/// other in any unexpected way...essentially all Bloom's data is safe from race conditions arising from
		/// server threads manipulating things on background threads. However, it makes it impossible for a new
		/// api call to interrupt a previous one. For example, when one api call is creating an epub preview
		/// and we get a new one saying we need to abort that (because one of the property buttons has changed),
		/// the epub that is being generated is obsolete and we want the new api call to go ahead so it can set a flag 
		/// to abort the one in progress. To avoid race conditions, api calls that set requiresSync false should be kept small
		/// and simple and be very careful about touching objects that other API calls might interact with.</param>
		public void RegisterEndpointHandler(string pattern, EndpointHandler handler, bool handleOnUiThread, bool requiresSync = true)
		{
			_endpointRegistrations[pattern.ToLowerInvariant().Trim(new char[] {'/'})] = new EndpointRegistration()
			{
				Handler = handler,
				HandleOnUIThread = handleOnUiThread,
				RequiresSync = requiresSync
			};
		}

		/// <summary>
		/// Handle simple boolean reads/writes
		/// </summary>
		public void RegisterBooleanEndpointHandler(string pattern, Func<ApiRequest, bool> readAction, Action<ApiRequest, bool> writeAction,
			bool handleOnUiThread, bool requiresSync = true)
		{
			RegisterEndpointHandler(pattern, request =>
			{
				if (request.HttpMethod == HttpMethods.Get)
				{
					request.ReplyWithBoolean(readAction(request));
				}
				else // post
				{
					writeAction(request, request.RequiredPostBooleanAsJson());
					request.PostSucceeded();
				}
			}, handleOnUiThread, requiresSync);
		}

		/// <summary>
		/// Samed as RegisterEndpointHandler, but for Endpoints that may be called by other endpoint handlers which are synchronous.
		/// If so, sets RequiresSync to false (because it would deadlock if a synchronous handler spawned off another synchronous handler)
		/// The caller should make sure that this endpoint handler can operate correctly without exclusive access to the lock!
		/// </summary>
		/// <param name="pattern">Simple string or regex to match APIs that this can handle. This must match what comes after the ".../api/" of the URL</param>
		/// <param name="handler">The method to call</param>
		/// <param name="handleOnUiThread">If true, the current thread will suspend until the UI thread can be used to call the method.
		/// This deliberately no longer has a default. It's something that should be thought about.
		/// Making it true can kill performance if you don't need it (BL-3452), and complicates exception handling and problem reporting (BL-4679).
		/// There's also danger of deadlock if something in the UI thread is somehow waiting for this request to complete.
		/// But, beware of race conditions or anything that manipulates UI controls if you make it false.</param>
		public void RegisterEndpointHandlerUsedByOthers(string pattern, EndpointHandler handler, bool handleOnUiThread)
		{
			_endpointRegistrations[pattern.ToLowerInvariant().Trim(new char[] {'/'})] = new EndpointRegistration()
			{
				Handler = handler,
				HandleOnUIThread = handleOnUiThread,
				RequiresSync = false
			};
		}

		/// <summary>
		/// Handle enum reads/writes
		/// </summary>
		public void RegisterEnumEndpointHandler<T>(string pattern, Func<ApiRequest, T> readAction, Action<ApiRequest, T> writeAction,
			bool handleOnUiThread, bool requiresSync = true)
		{
			Debug.Assert(typeof(T).IsEnum, "Type passed to RegisterEnumEndpointHandler is not an Enum.");
			RegisterEndpointHandler(pattern, request =>
			{
				if (request.HttpMethod == HttpMethods.Get)
				{
					request.ReplyWithEnum(readAction(request));
				}
				else // post
				{
					if (writeAction == null)
					{
						throw new ApplicationException($"Endpoint {pattern} is read only but received a post");
					}
					writeAction(request, request.RequiredPostEnumAsJson<T>());
					request.PostSucceeded();
				}
			}, handleOnUiThread, requiresSync);
		}

		public bool IsInvalidApiCall(string localPath)
		{
			// this 20 is just arbitrary... the point is, if it doesn't start with api/branding, it is bogus
			return localPath.IndexOf("api/branding", StringComparison.InvariantCulture) > 20;
		}

		// Every path should return false or send a response.
		// Otherwise we can get a timeout error as the browser waits for a response.
		//
		// NOTE: this method gets called on different threads!
		public bool ProcessRequest(IRequestInfo info, string localPath)
		{
			var localPathLc = localPath.ToLowerInvariant();
			if (localPathLc.StartsWith("api/", StringComparison.InvariantCulture))
			{
				var endpoint = localPath.Substring(3).ToLowerInvariant().Trim(new char[] {'/'});
				foreach (var pair in _endpointRegistrations.Where(pair =>
					Regex.Match(endpoint,
								"^" + //must match the beginning
								pair.Key.ToLower()
							).Success))
				{
					if (pair.Value.RequiresSync)
					{
						// A single synchronization object won't do, because when processing a request to create a thumbnail or update a preview,
						// we have to load the HTML page the thumbnail is based on, or other HTML pages (like one used to figure what's
						// visible in a preview). If the page content somehow includes
						// an api request (api/branding/image is one example), that request will deadlock if the
						// api/pageTemplateThumbnail request already has the main lock.
						// Another case is the Bloom Reader preview, where the whole UI is rebuilt at the same time as the preview.
						// This leads to multiple api requests racing with the preview one, and it was possible for all
						// the server threads to be processing these and waiting for SyncObject while the updatePreview
						// request held the lock...and the request for the page that would free the lock was sitting in
						// the queue, waiting for a thread.
						// To the best of my knowledge, there's no shared data between the thumbnailing and preview processes and any
						// other api requests, so it seems safe to have one lock that prevents working on multiple
						// thumbnails/previews at the same time, and one that prevents working on other api requests at the same time.
						var syncOn = SyncObj;
						if (localPathLc.StartsWith("api/pagetemplatethumbnail", StringComparison.InvariantCulture)
						|| localPathLc == "api/publish/android/thumbnail"
						|| localPathLc == "api/publish/android/updatepreview")
							syncOn = ThumbnailsAndPreviewsSyncObj;
						else if (localPathLc.StartsWith("api/i18n/"))
							syncOn = I18NLock;

						// Basically what lock(syncObj) {} is syntactic sugar for (see its documentation),
						// but we wrap RegisterThreadBlocking/Unblocked around acquiring the lock.
						// We need the more complicated structure because we would like RegisterThreadUnblocked
						// to be called immediately after acquiring the lock (notably, before Handle() is called),
						// but we also want to handle the case where Monitor.Enter throws an exception.
						bool lockAcquired = false;
						try						
						{
							// Try to acquire lock
							BloomServer._theOneInstance.RegisterThreadBlocking();
							try
							{
								// Blocks until it either succeeds (lockAcquired will then always be true) or throws (lockAcquired will stay false)
								Monitor.Enter(syncOn, ref lockAcquired);
							}
							finally
							{
								BloomServer._theOneInstance.RegisterThreadUnblocked();
							}

							// Lock has been acquired.

							ApiRequest.Handle(pair.Value, info, CurrentCollectionSettings, _bookSelection.CurrentSelection);
							// Even if ApiRequest.Handle() fails, return true to indicate that the request was processed and there
							// is no further need for the caller to continue trying to process the request as a filename.
							// See https://issues.bloomlibrary.org/youtrack/issue/BL-6763.
							return true;							
						}
						finally
						{
							if (lockAcquired)
							{
								Monitor.Exit(syncOn);
							}
						}
					}
					else
					{
						// Up to api's that request no sync to do things right!
						ApiRequest.Handle(pair.Value, info, CurrentCollectionSettings, _bookSelection.CurrentSelection);
						// Even if ApiRequest.Handle() fails, return true to indicate that the request was processed and there
						// is no further need for the caller to continue trying to process the request as a filename.
						// See https://issues.bloomlibrary.org/youtrack/issue/BL-6763.
						return true;
					}
				}
			}
			return false;
		}		
	}
}
