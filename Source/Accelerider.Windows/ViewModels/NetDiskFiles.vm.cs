﻿using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Input;
using Accelerider.Windows.Commands;
using Accelerider.Windows.Infrastructure;
using Accelerider.Windows.Infrastructure.Interfaces;
using Accelerider.Windows.ViewModels.Dialogs;
using Microsoft.Practices.Unity;
using System.Collections;
using System.Linq;
using Accelerider.Windows.Events;
using Accelerider.Windows.Assets;
using Accelerider.Windows.Views.Dialogs;
using MaterialDesignThemes.Wpf;
using System.Windows.Forms;
using System.Collections.ObjectModel;
using System;

namespace Accelerider.Windows.ViewModels
{
    public class NetDiskFilesViewModel : LoadingFilesBaseViewModel<ILazyTreeNode<INetDiskFile>>
    {
        private ILazyTreeNode<INetDiskFile> _currentFolder;

        private ICommand _enterFolderCommand;
        private ICommand _downloadCommand;
        private ICommand _uploadCommand;
        private ICommand _shareCommand;
        private ICommand _deleteCommand;


        public NetDiskFilesViewModel(IUnityContainer container) : base(container)
        {
            InitializeCommands();
        }


        public ILazyTreeNode<INetDiskFile> CurrentFolder
        {
            get => _currentFolder;
            set { if (SetProperty(ref _currentFolder, value)) RefreshFiles(); }
        }

        #region Commands
        public ICommand EnterFolderCommand
        {
            get => _enterFolderCommand;
            set => SetProperty(ref _enterFolderCommand, value);
        }
        public ICommand DownloadCommand
        {
            get => _downloadCommand;
            set => SetProperty(ref _downloadCommand, value);
        }
        public ICommand UploadCommand
        {
            get => _uploadCommand;
            set => SetProperty(ref _uploadCommand, value);
        }
        public ICommand ShareCommand
        {
            get => _shareCommand;
            set => SetProperty(ref _shareCommand, value);
        }
        public ICommand DeleteCommand
        {
            get => _deleteCommand;
            set => SetProperty(ref _deleteCommand, value);
        }


        private void InitializeCommands()
        {
            EnterFolderCommand = new RelayCommand<ILazyTreeNode<INetDiskFile>>(file => CurrentFolder = file, file => file?.Content?.FileType == FileTypeEnum.FolderType);
            DownloadCommand = new RelayCommand<IList>(DownloadCommandExecute, files => files != null && files.Count > 0);
            UploadCommand = new RelayCommand(UploadCommandExecute);
            ShareCommand = new RelayCommand<IList>(ShareCommandExecute, files => files != null && files.Count > 0);
            DeleteCommand = new RelayCommand<IList>(DeleteCommandExecute, files => files != null && files.Count > 0);
        }

        private async void DownloadCommandExecute(IList files)
        {
            var fileArray = files.Cast<ILazyTreeNode<INetDiskFile>>().ToArray();

            var (folder, isDownload) = await DisplayDownloadDialogAsync(fileArray.Select(item => item.Content.FilePath.FileName));

            if (!isDownload) return;

            var tokens = new List<ITransferTaskToken>();
            foreach (var file in fileArray)
            {
                await NetDiskUser.DownloadAsync(file, folder, token =>
                {
                    // Pulishes event
                    PulishTaskCreatedEvent<DownloadTaskCreatedEvent>(token, OnDownloaded);
                    // Records token
                    tokens.Add(token);
                });
            }

            var fileName = TrimFileName(tokens.First().FileSummary.FilePath.FileName, 40);
            var message = tokens.Count == 1
                ? string.Format(UiStrings.Message_AddedFileToDownloadList, fileName)
                : string.Format(UiStrings.Message_AddedFilesToDownloadList, fileName, tokens.Count);
            GlobalMessageQueue.Enqueue(message);
        }

        private async void UploadCommandExecute()
        {
            var dialog = new OpenFileDialog { Multiselect = true };
            if (dialog.ShowDialog() != DialogResult.OK || dialog.FileNames.Length <= 0) return;

            var tokens = new List<ITransferTaskToken>();
            await Task.Run(() =>
            {
                foreach (var fromPath in dialog.FileNames)
                {
                    var toPath = CurrentFolder.Content.FilePath;
                    var token = NetDiskUser.UploadAsync(fromPath, toPath);
                    PulishTaskCreatedEvent<UploadTaskCreatedEvent>(token, OnUploaded);
                    tokens.Add(token);
                }
            });

            var fileName = TrimFileName(dialog.FileNames[0], 40);
            var message = dialog.FileNames.Length == 1
                ? string.Format(UiStrings.Message_AddedFileToUploadList, fileName)
                : string.Format(UiStrings.Message_AddedFilesToUploadList, fileName, dialog.FileNames.Length);
            GlobalMessageQueue.Enqueue(message);
        }

        private async void ShareCommandExecute(IList files)
        {
            // 1. Display dialog.

            // 2. Determines whether to share based on the return value of dialog.

            var (code, shareSummary) = await NetDiskUser.ShareAsync(files.Cast<ILazyTreeNode<INetDiskFile>>().Select(node => node.Content));

            // 3. Sends the GlobalMessageQueue for reporting result.
        }

        private async void DeleteCommandExecute(IList files)
        {
            var currentFolder = CurrentFolder;
            var fileArray = files.Cast<ILazyTreeNode<INetDiskFile>>().ToArray();

            var errorFileCount = 0;
            foreach (var file in fileArray)
            {
                if (!await file.Content.DeleteAsync()) errorFileCount++;
            }
            if (errorFileCount < fileArray.Length)
            {
                await currentFolder.RefreshChildrenCacheAsync();
                if (currentFolder == CurrentFolder)
                {
                    OnPropertyChanged(nameof(CurrentFolder));
                }
            }
            GlobalMessageQueue.Enqueue($"({fileArray.Length - errorFileCount}/{fileArray.Length}) files have been deleted.");
        }
        #endregion

        protected override async Task<IEnumerable<ILazyTreeNode<INetDiskFile>>> GetFilesAsync()
        {
            if (PreviousNetDiskUser != NetDiskUser)
            {
                PreviousNetDiskUser = NetDiskUser;
                _currentFolder = await NetDiskUser.GetNetDiskFileRootAsync();
                OnPropertyChanged(nameof(CurrentFolder));
            }
            await CurrentFolder.RefreshChildrenCacheAsync();
            return CurrentFolder.ChildrenCache;
        }

        private async void RefreshFiles()
        {
            if (CurrentFolder.ChildrenCache == null)
                await LoadingFilesAsync();
            else
                Files = CurrentFolder.ChildrenCache;
        }

        private async Task<(string folder, bool isDownload)> DisplayDownloadDialogAsync(IEnumerable<string> files)
        {
            var configure = Container.Resolve<ILocalConfigureInfo>();
            if (configure.NotDisplayDownloadDialog) return (configure.DownloadDirectory, true);

            var dialog = new DownloadDialog();
            var vm = dialog.DataContext as DownloadDialogViewModel;
            vm.DownloadItems = files.ToList();

            if (!(bool)await DialogHost.Show(dialog, "RootDialog")) return (null, false);

            if (configure.NotDisplayDownloadDialog = vm.NotDisplayDownloadDialog)
            {
                configure.DownloadDirectory = vm.DownloadFolder;
            }
            return (vm.DownloadFolder, true);
        }

        private void PulishTaskCreatedEvent<T>(ITransferTaskToken token, EventHandler<TransferTaskStatusChangedEventArgs> handler)
            where T : TaskCreatedEvent, new()
        {
            token.TransferTaskStatusChanged += handler;
            EventAggregator.GetEvent<T>().Publish(token);
        }

        private void OnUploaded(object sender, TransferTaskStatusChangedEventArgs e)
        {
            if (e.NewStatus != TransferTaskStatusEnum.Completed) return;

            EventAggregator.GetEvent<UploadTaskEndEvent>().Publish(e.Token);
        }

        private void OnDownloaded(object sender, TransferTaskStatusChangedEventArgs e)
        {
            if (e.NewStatus != TransferTaskStatusEnum.Checking) return;

            EventAggregator.GetEvent<DownloadTaskEndEvent>().Publish(e.Token);
        }

        private string TrimFileName(string fileName, int length)
        {
            FileLocation fileLocation = fileName;
            var folderNameLength = length - fileLocation.FileName.Length - 5;
            return fileName.Length > length
                ? folderNameLength > 0
                    ? fileLocation.FolderPath.Substring(0, folderNameLength) + "...\\" + fileLocation.FileName
                    : fileLocation.FileName
                : fileName;
        }
    }
}
