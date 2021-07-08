﻿using Avalonia.Media;
using CarinaStudio.Collections;
using CarinaStudio.IO;
using CarinaStudio.Threading;
using CarinaStudio.ULogViewer.Converters;
using CarinaStudio.ULogViewer.Logs;
using CarinaStudio.ULogViewer.Logs.DataSources;
using CarinaStudio.ULogViewer.Logs.Profiles;
using CarinaStudio.ViewModels;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;

namespace CarinaStudio.ULogViewer.ViewModels
{
	/// <summary>
	/// Session to view logs.
	/// </summary>
	class Session : ViewModel
	{
		/// <summary>
		/// Property of <see cref="DisplayLogProperties"/>.
		/// </summary>
		public static readonly ObservableProperty<IList<DisplayableLogProperty>> DisplayLogPropertiesProperty = ObservableProperty.Register<Session, IList<DisplayableLogProperty>>(nameof(DisplayLogProperties), new DisplayableLogProperty[0]);
		/// <summary>
		/// Property of <see cref="HasLogs"/>.
		/// </summary>
		public static readonly ObservableProperty<bool> HasLogsProperty = ObservableProperty.Register<Session, bool>(nameof(HasLogs));
		/// <summary>
		/// Property of <see cref="Icon"/>.
		/// </summary>
		public static readonly ObservableProperty<Drawing?> IconProperty = ObservableProperty.Register<Session, Drawing?>(nameof(Icon));
		/// <summary>
		/// Property of <see cref="IsFilteringLogs"/>.
		/// </summary>
		public static readonly ObservableProperty<bool> IsFilteringLogsProperty = ObservableProperty.Register<Session, bool>(nameof(IsFilteringLogs));
		/// <summary>
		/// Property of <see cref="IsReadingLogs"/>.
		/// </summary>
		public static readonly ObservableProperty<bool> IsReadingLogsProperty = ObservableProperty.Register<Session, bool>(nameof(IsReadingLogs));
		/// <summary>
		/// Property of <see cref="IsProcessingLogs"/>.
		/// </summary>
		public static readonly ObservableProperty<bool> IsProcessingLogsProperty = ObservableProperty.Register<Session, bool>(nameof(IsProcessingLogs));
		/// <summary>
		/// Property of <see cref="LogProfile"/>.
		/// </summary>
		public static readonly ObservableProperty<LogProfile?> LogProfileProperty = ObservableProperty.Register<Session, LogProfile?>(nameof(LogProfile));
		/// <summary>
		/// Property of <see cref="Logs"/>.
		/// </summary>
		public static readonly ObservableProperty<IList<DisplayableLog>> LogsProperty = ObservableProperty.Register<Session, IList<DisplayableLog>>(nameof(Logs), new DisplayableLog[0]);
		/// <summary>
		/// Property of <see cref="Title"/>.
		/// </summary>
		public static readonly ObservableProperty<string?> TitleProperty = ObservableProperty.Register<Session, string?>(nameof(Title));


		// Fields.
		readonly HashSet<string> addedFilePaths = new HashSet<string>(PathEqualityComparer.Default);
		readonly SortedObservableList<DisplayableLog> allLogs;
		readonly MutableObservableBoolean canAddFile = new MutableObservableBoolean();
		readonly MutableObservableBoolean canResetLogProfile = new MutableObservableBoolean();
		readonly MutableObservableBoolean canSetLogProfile = new MutableObservableBoolean();
		readonly MutableObservableBoolean canSetWorkingDirectory = new MutableObservableBoolean();
		Comparison<DisplayableLog?> compareDisplayableLogsDelegate;
		DisplayableLogGroup? displayableLogGroup;
		IComparer<Log> logComparer = LogComparers.TimestampAsc;
		readonly HashSet<LogReader> logReaders = new HashSet<LogReader>();
		readonly ScheduledAction updateIsReadingLogsAction;
		readonly ScheduledAction updateIsProcessingLogsAction;
		readonly ScheduledAction updateTitleAndIconAction;


		/// <summary>
		/// Initialize new <see cref="Session"/> instance.
		/// </summary>
		/// <param name="workspace"><see cref="Workspace"/>.</param>
		public Session(Workspace workspace) : base(workspace.Application)
		{
			// create commands
			this.AddLogFileCommand = ReactiveCommand.Create<string?>(this.AddLogFile, this.canAddFile);
			this.ResetLogProfileCommand = ReactiveCommand.Create(this.ResetLogProfile, this.canResetLogProfile);
			this.SetLogProfileCommand = ReactiveCommand.Create<LogProfile?>(this.SetLogProfile, this.canSetLogProfile);
			this.SetWorkingDirectoryCommand = ReactiveCommand.Create<string?>(this.SetWorkingDirectory, this.canSetWorkingDirectory);
			this.canSetLogProfile.Update(true);

			// create collections
			this.allLogs = new SortedObservableList<DisplayableLog>(this.CompareDisplayableLogs).Also(it =>
			{
				it.CollectionChanged += this.OnAllLogsChanged;
			});

			// setup properties
			this.SetValue(LogsProperty, this.allLogs.AsReadOnly());
			this.Workspace = workspace;

			// setup delegates
			this.compareDisplayableLogsDelegate = this.CompareDisplayableLogsAsc;

			// create scheduled actions
			this.updateIsReadingLogsAction = new ScheduledAction(() =>
			{
				if (this.IsDisposed)
					return;
				if (this.logReaders.IsEmpty())
					this.SetValue(IsReadingLogsProperty, false);
				else
				{
					foreach (var logReader in this.logReaders)
					{
						switch (logReader.State)
						{
							case LogReaderState.Starting:
							case LogReaderState.ReadingLogs:
								this.SetValue(IsReadingLogsProperty, true);
								return;
						}
					}
					this.SetValue(IsReadingLogsProperty, false);
				}
			});
			this.updateIsProcessingLogsAction = new ScheduledAction(() =>
			{
				if (this.IsDisposed)
					return;
				var logProfile = this.LogProfile;
				if (logProfile == null || logProfile.IsContinuousReading)
					this.SetValue(IsProcessingLogsProperty, false);
				else if (this.IsFilteringLogs || this.IsReadingLogs)
					this.SetValue(IsProcessingLogsProperty, true);
				else
					this.SetValue(IsProcessingLogsProperty, false);
			});
			this.updateTitleAndIconAction = new ScheduledAction(() =>
			{
				// check state
				if (this.IsDisposed)
					return;

				// select icon
				var app = this.Application as App;
				var logProfile = this.LogProfile;
				var icon = Global.Run(() =>
				{
					if (logProfile == null)
					{
						var res = (object?)null;
						app?.Resources?.TryGetResource("Drawing.EmptySession", out res);
						return res as Drawing;
					}
					else if (app != null)
						return LogProfileIconConverter.Default.Convert(logProfile.Icon, typeof(Drawing), null, app.CultureInfo) as Drawing;
					return null;
				});

				// select title
				var title = Global.Run(() =>
				{
					if (logProfile == null)
						return app?.GetString("Session.Empty");
					if (logProfile.DataSourceProvider.UnderlyingSource != UnderlyingLogDataSource.File || this.addedFilePaths.IsEmpty())
						return logProfile.Name;
					return $"{logProfile.Name} ({this.addedFilePaths.Count})";
				});

				// update properties
				this.SetValue(IconProperty, icon);
				this.SetValue(TitleProperty, title);
			});
		}


		// Add file.
		void AddLogFile(string? fileName)
		{
			// check parameter and state
			this.VerifyAccess();
			this.VerifyDisposed();
			if (!this.canAddFile.Value)
				return;
			if (fileName == null)
				throw new ArgumentNullException(nameof(fileName));
			var profile = this.LogProfile ?? throw new InternalStateCorruptedException("No log profile to add log file.");
			if (profile.DataSourceProvider.UnderlyingSource != UnderlyingLogDataSource.File)
				throw new InternalStateCorruptedException($"Cannot add log file because underlying data source type is {profile.DataSourceProvider.UnderlyingSource}.");
			var dataSourceOptions = profile.DataSourceOptions;
			if (dataSourceOptions.FileName != null)
				throw new InternalStateCorruptedException($"Cannot add log file because file name is already specified.");
			if (!this.addedFilePaths.Add(fileName))
			{
				this.Logger.LogWarning($"File '{fileName}' is already added");
				return;
			}

			this.Logger.LogDebug($"Add log file '{fileName}'");

			// create data source
			dataSourceOptions.FileName = fileName;
			var dataSource = this.CreateLogDataSourceOrNull(profile.DataSourceProvider, dataSourceOptions);
			if (dataSource == null)
				return;

			// create log reader
			this.CreateLogReader(dataSource);

			// update title
			this.updateTitleAndIconAction.Schedule();
		}


		/// <summary>
		/// Command to add log file.
		/// </summary>
		/// <remarks>Type of command parameter is <see cref="string"/>.</remarks>
		public ICommand AddLogFileCommand { get; }


		// Compare displayable logs.
		int CompareDisplayableLogs(DisplayableLog? x, DisplayableLog? y) => this.compareDisplayableLogsDelegate(x, y);
		int CompareDisplayableLogsAsc(DisplayableLog? x, DisplayableLog? y)
		{
			// compare by reference
			if (x == null)
			{
				if (y == null)
					return 0;
				return -1;
			}
			if (y == null)
				return 1;

			// compare by timestamp
			var diff = x.BinaryTimestamp - y.BinaryTimestamp;
			if (diff < 0)
				return -1;
			if (diff > 0)
				return 1;

			// compare by ID
			diff = x.LogId - y.LogId;
			if (diff < 0)
				return -1;
			if (diff > 0)
				return 1;
			return 0;
		}
		int CompareDisplayableLogsDesc(DisplayableLog? x, DisplayableLog? y)
		{
			// compare by reference
			if (x == null)
			{
				if (y == null)
					return 0;
				return -1;
			}
			if (y == null)
				return 1;

			// compare by timestamp
			var diff = x.BinaryTimestamp - y.BinaryTimestamp;
			if (diff < 0)
				return 1;
			if (diff > 0)
				return -1;

			// compare by ID
			diff = x.LogId - y.LogId;
			if (diff < 0)
				return 1;
			if (diff > 0)
				return -1;
			return 0;
		}


		// Try creating log data source.
		ILogDataSource? CreateLogDataSourceOrNull(ILogDataSourceProvider provider, LogDataSourceOptions options)
		{
			try
			{
				return provider.CreateSource(options);
			}
			catch (Exception ex)
			{
				this.Logger.LogError(ex, $"Unable to create data source");
				return null;
			}
		}


		// Create log reader.
		void CreateLogReader(ILogDataSource dataSource)
		{
			// create log reader
			var profile = this.LogProfile ?? throw new InternalStateCorruptedException("No log profile to create log reader.");
			var logReader = new LogReader(dataSource).Also(it =>
			{
				it.LogLevelMap = profile.LogLevelMap;
				it.LogPatterns = profile.LogPatterns;
				it.TimestampCultureInfo = profile.TimestampCultureInfoForReading;
				it.TimestampFormat = profile.TimestampFormatForReading;
			});
			this.logReaders.Add(logReader);
			this.Logger.LogDebug($"Log reader '{logReader.Id} created");

			// add event handlers
			dataSource.PropertyChanged += this.OnLogDataSourcePropertyChanged;
			logReader.LogsChanged += this.OnLogReaderLogsChanged;
			logReader.PropertyChanged += this.OnLogReaderPropertyChanged;

			// start reading logs
			logReader.Start();

			// add logs
			var displayableLogGroup = this.displayableLogGroup ?? throw new InternalStateCorruptedException("No displayable log group.");
			this.allLogs.AddAll(logReader.Logs.Let(logs =>
			{
				return new DisplayableLog[logs.Count].Also(array =>
				{
					for (var i = array.Length - 1; i >= 0; --i)
						array[i] = displayableLogGroup.CreateDisplayableLog(logReader, logs[i]);
				});
			}));
		}


		/// <summary>
		/// Get list of property of <see cref="DisplayableLog"/> needed to be shown on UI.
		/// </summary>
		public IList<DisplayableLogProperty> DisplayLogProperties { get => this.GetValue(DisplayLogPropertiesProperty); }


		// Dispose.
		protected override void Dispose(bool disposing)
		{
			// ignore managed resources
			if (!disposing)
			{
				base.Dispose(disposing);
				return;
			}

			// check thread
			this.VerifyAccess();

			// cancel filtering
			//

			// dispose log readers
			this.DisposeLogReaders(false);

			// clear logs
			foreach (var displayableLog in this.allLogs)
				displayableLog.Dispose();
			this.allLogs.Clear();
			this.displayableLogGroup = this.displayableLogGroup.DisposeAndReturnNull();

			// detach from log profile
			this.LogProfile?.Let(it => it.PropertyChanged -= this.OnLogProfilePropertyChanged);

			// call base
			base.Dispose(disposing);
		}


		// Dispose log reader.
		void DisposeLogReader(LogReader logReader, bool removeLogs)
		{
			// remove from set
			if (!this.logReaders.Remove(logReader))
				return;

			// remove event handlers
			var dataSource = logReader.DataSource;
			dataSource.PropertyChanged -= this.OnLogDataSourcePropertyChanged;
			logReader.LogsChanged -= this.OnLogReaderLogsChanged;
			logReader.PropertyChanged -= this.OnLogReaderPropertyChanged;

			// remove logs
			if (removeLogs)
				this.allLogs.RemoveAll(it => it.LogReader == logReader);

			// dispose data source and log reader
			logReader.Dispose();
			dataSource.Dispose();
			this.Logger.LogDebug($"Log reader '{logReader.Id} disposed");
		}


		// Dispose all log readers.
		void DisposeLogReaders(bool removeLogs)
		{
			foreach (var logReader in this.logReaders.ToArray())
				this.DisposeLogReader(logReader, removeLogs);
			this.updateIsReadingLogsAction.Execute();
			this.updateIsProcessingLogsAction.Execute();
		}


		/// <summary>
		/// Check whether at least one log is read or not.
		/// </summary>
		public bool HasLogs { get => this.GetValue(HasLogsProperty); }


		/// <summary>
		/// Get icon of session.
		/// </summary>
		public Drawing? Icon { get => this.GetValue(IconProperty); }


		/// <summary>
		/// Check whether logs are being filtered or not.
		/// </summary>
		public bool IsFilteringLogs { get => this.GetValue(IsFilteringLogsProperty); }


		/// <summary>
		/// Check whether logs are being read or not.
		/// </summary>
		public bool IsReadingLogs { get => this.GetValue(IsReadingLogsProperty); }


		/// <summary>
		/// Check whether logs are being processed or not.
		/// </summary>
		public bool IsProcessingLogs { get => this.GetValue(IsProcessingLogsProperty); }


		/// <summary>
		/// Get current log profile.
		/// </summary>
		public LogProfile? LogProfile { get => this.GetValue(LogProfileProperty); }


		/// <summary>
		/// Get list of <see cref="DisplayableLog"/>s to display.
		/// </summary>
		public IList<DisplayableLog> Logs { get => this.GetValue(LogsProperty); }


		// Called when logs in allLogs has been changed.
		void OnAllLogsChanged(object? sender, NotifyCollectionChangedEventArgs e)
		{
			switch (e.Action)
			{
				case NotifyCollectionChangedAction.Add:
					break;
				case NotifyCollectionChangedAction.Remove:
					foreach (DisplayableLog displayableLog in e.OldItems.AsNonNull())
						displayableLog.Dispose();
					break;
			}
			if (!this.IsDisposed)
				this.SetValue(HasLogsProperty, this.allLogs.IsNotEmpty());
		}


		// Called when property of log data source changed.
		void OnLogDataSourcePropertyChanged(object? sender, PropertyChangedEventArgs e)
		{ }


		// Called when property of log profile changed.
		void OnLogProfilePropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (sender != this.LogProfile)
				return;
			if (e.PropertyName == nameof(LogProfile.Name))
				this.updateTitleAndIconAction.Schedule();
		}


		// Called when logs of log reader has been changed.
		void OnLogReaderLogsChanged(object? sender, NotifyCollectionChangedEventArgs e)
		{
			var logReader = (LogReader)sender.AsNonNull();
			switch (e.Action)
			{
				case NotifyCollectionChangedAction.Add:
					this.allLogs.AddAll(e.NewItems.AsNonNull().Let(logs=>
					{
						var displayableLogGroup = this.displayableLogGroup ?? throw new InternalStateCorruptedException("No displayable log group.");
						return new DisplayableLog[logs.Count].Also(array =>
						{
							for (var i = array.Length - 1; i >= 0; --i)
								array[i] = displayableLogGroup.CreateDisplayableLog(logReader, (Log)logs[i].AsNonNull());
						});
					}));
					break;
				case NotifyCollectionChangedAction.Remove:
					e.OldItems.AsNonNull().Let(oldItems =>
					{
						if (oldItems.Count == 1)
						{
							var removedLog = (Log)oldItems[0].AsNonNull();
							this.allLogs.RemoveAll(it => it.Log == removedLog);
						}
						else
						{
							var removedLogs = new HashSet<Log>((IEnumerable<Log>)oldItems);
							this.allLogs.RemoveAll(it => removedLogs.Contains(it.Log));
						}
					});
					break;
				case NotifyCollectionChangedAction.Reset:
					this.allLogs.RemoveAll(it => it.LogReader == logReader);
					break;
				default:
					throw new InvalidOperationException($"Unsupported logs change action: {e.Action}.");
			}
		}


		// Called when property of log reader changed.
		void OnLogReaderPropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			switch (e.PropertyName)
			{
				case nameof(LogReader.State):
					this.updateIsReadingLogsAction.Schedule();
					break;
			}
		}


		// Called when property changed.
		protected override void OnPropertyChanged(ObservableProperty property, object? oldValue, object? newValue)
		{
			base.OnPropertyChanged(property, oldValue, newValue);
			if (property == IsFilteringLogsProperty || property == IsReadingLogsProperty)
				this.updateIsProcessingLogsAction.Schedule();
		}


		// Reset log profile.
		void ResetLogProfile()
		{
			// check state
			this.VerifyAccess();
			this.VerifyDisposed();
			if (!this.canResetLogProfile.Value)
				return;
			var profile = this.LogProfile;
			if (profile == null)
				throw new InternalStateCorruptedException("No log profile to reset.");

			// detach from log profile
			profile.PropertyChanged -= this.OnLogProfilePropertyChanged;

			// clear profile
			this.Logger.LogWarning($"Reset log profile '{profile.Name}'");
			this.SetValue(LogProfileProperty, null);

			// cancel filtering
			//

			// dispose log readers
			this.DisposeLogReaders(false);

			// clear logs
			foreach (var displayableLog in this.allLogs)
				displayableLog.Dispose();
			this.allLogs.Clear();
			this.displayableLogGroup = this.displayableLogGroup.DisposeAndReturnNull();

			// clear file name table
			this.addedFilePaths.Clear();

			// clear display log properties
			this.UpdateDisplayLogProperties();

			// update title
			this.updateTitleAndIconAction.Schedule();

			// update state
			this.canAddFile.Update(false);
			this.canSetWorkingDirectory.Update(false);
			this.canResetLogProfile.Update(false);
			this.canSetLogProfile.Update(true);
		}


		/// <summary>
		/// Command to reset log profile.
		/// </summary>
		public ICommand ResetLogProfileCommand { get; }


		// Set log profile.
		void SetLogProfile(LogProfile? profile)
		{
			// check parameter and state
			this.VerifyAccess();
			this.VerifyDisposed();
			if (!this.canSetLogProfile.Value)
				return;
			if (profile == null)
				throw new ArgumentNullException(nameof(profile));
			if (this.LogProfile != null)
				throw new InternalStateCorruptedException("Already set another log profile.");

			// select profile
			this.Logger.LogWarning($"Set profile '{profile.Name}'");
			this.canSetLogProfile.Update(false);
			this.SetValue(LogProfileProperty, profile);

			// attach to log profile
			profile.PropertyChanged += this.OnLogProfilePropertyChanged;

			// prepare displayable log group
			this.displayableLogGroup = new DisplayableLogGroup(profile);

			// setup log comparer
			if (profile.SortDirection == SortDirection.Ascending)
				this.compareDisplayableLogsDelegate = this.CompareDisplayableLogsAsc;
			else
				this.compareDisplayableLogsDelegate = this.CompareDisplayableLogsDesc;

			// read logs or wait for more actions
			var dataSourceOptions = profile.DataSourceOptions;
			var dataSourceProvider = profile.DataSourceProvider;
			switch (dataSourceProvider.UnderlyingSource)
			{
				case UnderlyingLogDataSource.File:
					if (dataSourceOptions.FileName == null)
					{
						this.Logger.LogDebug("No file name specified, waiting for opening file");
						this.canAddFile.Update(true);
					}
					else
					{
						this.Logger.LogDebug("File name specified, start reading logs");
						var dataSource = this.CreateLogDataSourceOrNull(dataSourceProvider, dataSourceOptions);
						if (dataSource != null)
							this.CreateLogReader(dataSource);
					}
					break;
				case UnderlyingLogDataSource.StandardOutput:
					if (dataSourceOptions.Command == null)
						this.Logger.LogError("No command to open standard output");
					else if (dataSourceOptions.WorkingDirectory == null && profile.IsWorkingDirectoryNeeded)
					{
						this.Logger.LogDebug("Need working directory, waiting for setting working directory");
						this.canSetWorkingDirectory.Update(true);
					}
					else
					{
						this.Logger.LogDebug("Start reading logs from standard output");
						var dataSource = this.CreateLogDataSourceOrNull(dataSourceProvider, dataSourceOptions);
						if (dataSource != null)
							this.CreateLogReader(dataSource);
					}
					break;
				default:
					this.Logger.LogError($"Unsupported underlying log data source type: {dataSourceProvider.UnderlyingSource}");
					break;
			}

			// update display log properties
			this.UpdateDisplayLogProperties();

			// update title
			this.updateTitleAndIconAction.Schedule();

			// update state
			this.canResetLogProfile.Update(true);
		}


		/// <summary>
		/// Command to set specific log profile.
		/// </summary>
		/// <remarks>Type of command parameter is <see cref="LogProfile"/>.</remarks>
		public ICommand SetLogProfileCommand { get; }


		// Set working directory.
		void SetWorkingDirectory(string? directory)
		{
			// check parameter and state
			this.VerifyAccess();
			this.VerifyDisposed();
			if (!this.canSetWorkingDirectory.Value)
				return;
			if (directory == null)
				throw new ArgumentNullException(nameof(directory));
			var profile = this.LogProfile ?? throw new InternalStateCorruptedException("No log profile to set working directory.");
			if (profile.DataSourceProvider.UnderlyingSource != UnderlyingLogDataSource.StandardOutput)
				throw new InternalStateCorruptedException($"Cannot set working directory because underlying data source type is {profile.DataSourceProvider.UnderlyingSource}.");
			var dataSourceOptions = profile.DataSourceOptions;
			if (dataSourceOptions.WorkingDirectory != null)
				throw new InternalStateCorruptedException($"Cannot set working directory because working directory is already specified.");

			// check current working directory
			if (this.logReaders.IsNotEmpty())
			{
				if (PathEqualityComparer.Default.Equals(this.logReaders.First().DataSource.CreationOptions.WorkingDirectory, directory))
				{
					this.Logger.LogDebug("Set to same working directory");
					return;
				}
			}

			// dispose current log readers
			this.DisposeLogReaders(true);

			this.Logger.LogDebug($"Set working directory to '{directory}'");

			// create data source
			dataSourceOptions.WorkingDirectory = directory;
			var dataSource = this.CreateLogDataSourceOrNull(profile.DataSourceProvider, dataSourceOptions);
			if (dataSource == null)
				return;

			// create log reader
			this.CreateLogReader(dataSource);
		}


		/// <summary>
		/// Command to set working directory.
		/// </summary>
		/// <remarks>Type of command parameter is <see cref="string"/>.</remarks>
		public ICommand SetWorkingDirectoryCommand { get; }


		/// <summary>
		/// Get title of session.
		/// </summary>
		string? Title { get => this.GetValue(TitleProperty); }


		// Update list of display log properties according to profile.
		void UpdateDisplayLogProperties()
		{
			var profile = this.LogProfile;
			if (profile == null)
				this.SetValue(DisplayLogPropertiesProperty, DisplayLogPropertiesProperty.DefaultValue);
			else
			{
				var app = (IApplication)this.Application;
				var visibleLogProperties = profile.VisibleLogProperties;
				var displayLogProperties = new List<DisplayableLogProperty>();
				foreach (var logProperty in visibleLogProperties)
					displayLogProperties.Add(new DisplayableLogProperty(app, logProperty));
				if (displayLogProperties.IsEmpty())
					displayLogProperties.Add(new DisplayableLogProperty(app, nameof(DisplayableLog.Message), null, null));
				this.SetValue(DisplayLogPropertiesProperty, displayLogProperties.AsReadOnly());
			}
		}


		/// <summary>
		/// Get <see cref="Workspace"/> which session belongs to.
		/// </summary>
		public Workspace Workspace { get; }
	}
}
