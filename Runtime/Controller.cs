
using System;
using UdonSharp;
using UnityEngine;
using VRC.SDK3.Components.Video;
using VRC.SDKBase;

#if AUDIOLINK_V1
using AudioLink;
#endif

namespace Yamadev.YamaStream
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public partial class Controller : Listener
    {
        [SerializeField] string _version;
        [SerializeField] Animator _videoPlayerAnimator;
        [SerializeField] VideoPlayerHandle[] _videoPlayerHandles;
        [SerializeField] Permission _permission;
        [SerializeField] float _retryAfterSeconds = 5.1f;
        [SerializeField] int _maxErrorRetry = 5;
        [SerializeField] string _timeFormat = @"hh\:mm\:ss";
        [SerializeField, UdonSynced, FieldChangeCallback(nameof(VideoPlayerType))] VideoPlayerType _videoPlayerType;
        [SerializeField, UdonSynced, FieldChangeCallback(nameof(Loop))] bool _loop;
        [UdonSynced, FieldChangeCallback(nameof(Paused))] bool _paused;
        [UdonSynced, FieldChangeCallback(nameof(Stopped))] bool _stopped = true;
        [UdonSynced, FieldChangeCallback(nameof(Speed))] float _speed = 1f;
        [UdonSynced, FieldChangeCallback(nameof(Repeat))] Vector3 _repeat = new Vector3(0f, 0f, 999999f);
        VideoPlayerType _videoPlayerTypeLocal;
        bool _loopLocal;
        bool _pausedLocal;
        bool _stoppedLocal = true;
        float _speedLocal;
        Vector3 _repeatLocal;
        Listener[] _listeners = { };
        bool _isLocal;
        int _errorRetryCount = 0;
        bool _loading;
        bool _isReload;
        float _lastSetTime = 0f;
        float _repeatCooling = 0.6f; 
        bool _initialized;

        void Start() => Initialize();

        void Update()
        {
            if (OutOfRepeat(VideoTime) && Time.time - _lastSetTime > _repeatCooling) 
                SetTime(Repeat.ToRepeatStatus().GetStartTime());
            if (!_isLocal && IsPlaying && Time.time - _syncFrequency > _lastSync) DoSync();
        }

        public string Version => _version;

        public Permission Permission => _permission;
        public PlayerPermission PlayerPermission => _permission == null ? PlayerPermission.Editor : _permission.PlayerPermission;

        public void Initialize()
        {
            if (_initialized) return;
            Loop = _loop;
            _videoPlayerAnimator.Rebind();
            initializeScreen();
            UpdateAudio();
            UpdateAudioLink();
            foreach (VideoPlayerHandle handle in _videoPlayerHandles)
                handle.Listener = this;
            _initialized = true;
        }

        public void AddListener(Listener listener)
        {
            if (Array.IndexOf(_listeners, listener) >= 0) return;
            _listeners = _listeners.Add(listener);
        }

        public bool IsLocal
        {
            get => _isLocal;
            set
            {
                if (_isLocal == value) return;
                if (!value) Stopped = true;
                _isLocal = value;
                if (value) Stopped = true;
                else
                {
                    Track = Track.New(_targetPlayer, _title, _url, _originalUrl);
                    Reload();
                }
                foreach (Listener listener in _listeners) listener.OnLocalModeChanged();
            }
        }

        public VideoPlayerType VideoPlayerType
        {
            get => _isLocal ? _videoPlayerTypeLocal : _videoPlayerType;
            set 
            {
                if ((_isLocal ? _videoPlayerTypeLocal : _videoPlayerType) == value) return;
                VideoPlayerHandle.Stop();
                if (_isLocal) _videoPlayerTypeLocal = value;
                else _videoPlayerType = value;
                if (Networking.IsOwner(gameObject) && !_isLocal) RequestSerialization();
                foreach (Listener listener in _listeners) listener.OnPlayerChanged();
            }
        }

        public VideoPlayerHandle VideoPlayerHandle
        {
            get
            {
                foreach (VideoPlayerHandle handle in _videoPlayerHandles) 
                    if (handle.VideoPlayerType == _videoPlayerType) return handle;
                return null;
            }
        }

        public bool Paused
        {
            get => _isLocal ? _pausedLocal : _paused;
            set
            {
                if (_isLocal) _pausedLocal = value;
                else _paused = value;
                if (value) VideoPlayerHandle.Pause();
                else VideoPlayerHandle.Play();
#if AUDIOLINK_V1
                if (_audioLink != null && _useAudioLink)
                    _audioLink.SetMediaPlaying(value ? MediaPlaying.Paused : IsLive ? MediaPlaying.Streaming : MediaPlaying.Playing);
#endif
                if (Networking.IsOwner(gameObject) && !_isLocal)
                {
                    SyncTime = VideoTime - VideoStandardDelay;
                    RequestSerialization();
                }
            }
        }

        public bool Stopped
        {
            get => _isLocal ? _stoppedLocal : _stopped;
            set
            {
                if (_isLocal) _stoppedLocal = value;
                else _stopped = value;
                _isReload = false;
                if (value) VideoPlayerHandle.Stop();
                if (Networking.IsOwner(gameObject) && !_isLocal) RequestSerialization();
            }
        }

        public bool Loop
        {
            get => _isLocal ? _loopLocal : _loop;
            set
            {
                if (_isLocal) _loopLocal = value;
                else _loop = value;
                foreach (VideoPlayerHandle handle in _videoPlayerHandles) handle.Loop = value;
#if AUDIOLINK_V1
                if (_audioLink != null && _useAudioLink)
                    _audioLink.SetMediaLoop(_loop ? MediaLoop.LoopOne : MediaLoop.None);
#endif
                if (Networking.IsOwner(gameObject) && !_isLocal) RequestSerialization();
                foreach (Listener listener in _listeners) listener.OnLoopChanged();
            }
        }

        public void UpdateSpeed()
        {
            _videoPlayerAnimator.SetFloat("Speed", Speed);
            _videoPlayerAnimator.Update(0f);
            if (!Stopped && VideoPlayerType == VideoPlayerType.AVProVideoPlayer) 
                SendCustomEventDelayedFrames(nameof(Reload), 1);
            UpdateAudio();
        }

        public float Speed
        {
            get => _isLocal ? _speedLocal : _speed;
            set
            {
                if (_isLocal) _speedLocal = value;
                else _speed = value;
                UpdateSpeed();
                if (Networking.IsOwner(gameObject) && !_isLocal)
                {
                    SyncTime = VideoTime - VideoStandardDelay;
                    RequestSerialization();
                }
                foreach (Listener listener in _listeners) listener.OnSpeedChanged();
            }
        }

        public bool OutOfRepeat(float targetTime)
        {
            if (!IsPlaying || !Repeat.ToRepeatStatus().IsOn()) return false;
            return targetTime > Repeat.ToRepeatStatus().GetEndTime() || targetTime < Repeat.ToRepeatStatus().GetStartTime();

        }

        public Vector3 Repeat
        {
            get => _isLocal ? _repeatLocal : _repeat;
            set
            {
                if (_isLocal) _repeatLocal = value;
                else _repeat = value;
                if (Networking.IsOwner(gameObject) && !_isLocal) RequestSerialization();
                foreach (Listener listener in _listeners) listener.OnRepeatChanged();
            }
        }

        public float LastLoaded => VideoPlayerHandle.LastLoaded;
        public bool IsPlaying => VideoPlayerHandle.IsPlaying;
        public float Duration => VideoPlayerHandle.Duration;
        public float VideoTime => VideoPlayerHandle.Time;
        public bool IsLoading => _loading;
        public bool IsReload => _isReload;
        public bool IsLive => float.IsInfinity(Duration);

        public void Reload()
        {
            if (!Stopped && !IsLoading) PlayTrack(Track, true);
        }

        public void ErrorRetry()
        {
            if (IsPlaying || !Track.GetUrl().IsValidUrl()) return;
            if (Time.time - LastLoaded < _retryAfterSeconds)
            {
                SendCustomEventDelayedFrames(nameof(ErrorRetry), 0);
                return;
            }
            _loading = true;
            _resolveTrack.Invoke();
            foreach (Listener listener in _listeners) listener.OnVideoRetry();
        }

        public void SetTime(float time)
        {
            if (IsLive || OutOfRepeat(time)) return;
            VideoPlayerHandle.Time = time;
            _lastSetTime = Time.time;
            if (Networking.IsOwner(gameObject) && !_isLocal)
            {
                SyncTime = time - VideoStandardDelay;
                RequestSerialization();
            }
            foreach (Listener listener in _listeners) listener.OnSetTime(time);
        }

        public void SendCustomVideoEvent(string eventName)
        {
            foreach (Listener listener in _listeners) listener.SendCustomEvent(eventName);
        }

        public override void OnDeserialization()
        {
            if (_isLocal) return;
            Track track = Track.New(_targetPlayer, _title, _url, _originalUrl);
            foreach (Listener listener in _listeners) listener.OnTrackSynced(track.GetUrl());
            if (track.GetUrl() != Track.GetUrl())
            {
                Stopped = true;
                PlayTrack(track);
            }
            DoSync(true);
        }

        #region Video Event
        public override void OnVideoReady()
        {
            _loading = false;
            foreach (Listener listener in _listeners) listener.OnVideoReady();
        }

        public override void OnVideoStart() 
        {
            _errorRetryCount = 0;
            _loading = false;
            if (_isLocal) _stoppedLocal = false;
            else _stopped = false;
            if (Paused) VideoPlayerHandle.Pause();
            else VideoPlayerHandle.Play();
            UpdateAudio();
#if AUDIOLINK_V1
            if (_audioLink != null && _useAudioLink)
                _audioLink.SetMediaPlaying(IsLive ? MediaPlaying.Streaming : MediaPlaying.Playing);
#endif
            if (!_isLocal)
            {
                if (Networking.IsOwner(gameObject) && !_isReload)
                {
                    SyncTime = 0f;
                    RequestSerialization();
                }
                else DoSync();
            }
            if (KaraokeMode != KaraokeMode.None) SendCustomEventDelayedSeconds(nameof(ForceSync), 1f);
            foreach (Listener listener in _listeners) listener.OnVideoStart();
            _isReload = false;
        }

        public override void OnVideoPlay()
        {
            if (_isLocal) _pausedLocal = false;
            else _paused = false;
            if (KaraokeMode != KaraokeMode.None) SendCustomEventDelayedSeconds(nameof(ForceSync), 1f);
            foreach (Listener listener in _listeners) listener.OnVideoPlay();
        }

        public override void OnVideoPause()
        {
            if (_isLocal) _pausedLocal = true;
            else _paused = true;
            foreach (Listener listener in _listeners) listener.OnVideoPause();
        }

        public override void OnVideoStop()
        {
            if (!_isReload)
            {
                if (_isLocal) _pausedLocal = false;
                else _paused = false;
                _loading = false;
                if (_isLocal) _stoppedLocal = true;
                else _stopped = true;
                _errorRetryCount = 0;
                if (_isLocal) _repeatLocal = new Vector3(0f, 0f, 999999f);
                else _repeat = new Vector3(0f, 0f, 999999f);
                if (!string.IsNullOrEmpty(Track.GetUrl())) _history.AddTrack(Track);
                Track = Track.New(_videoPlayerType, string.Empty, VRCUrl.Empty);
#if AUDIOLINK_V1
                if (_audioLink != null && _useAudioLink)
                    _audioLink.SetMediaPlaying(MediaPlaying.Stopped);
#endif
                if (Networking.IsOwner(gameObject) && !_isLocal)
                {
                    ClearSync();
                    RequestSerialization();
                }
            }
            foreach (Listener listener in _listeners) listener.OnVideoStop();
        }

        public override void OnVideoLoop()
        {
            if (Networking.IsOwner(gameObject) && !_isLocal)
            {
                SyncTime = 0f;
                RequestSerialization();
            }
            foreach (Listener listener in _listeners) listener.OnVideoLoop();
        }
        public override void OnVideoEnd()
        {
            if (Networking.IsOwner(gameObject) && !_isLocal && _forwardInterval >= 0)
                SendCustomEventDelayedSeconds(nameof(RunForward), _forwardInterval);
            foreach (Listener listener in _listeners) listener.OnVideoEnd();
        }

        public override void OnVideoError(VideoError videoError)
        {
            _loading = false;
#if AUDIOLINK_V1
            if (_audioLink != null && _useAudioLink)
                _audioLink.SetMediaPlaying(MediaPlaying.Error);
#endif
            if (videoError != VideoError.AccessDenied)
            {
                if (_errorRetryCount < _maxErrorRetry)
                {
                    _errorRetryCount++;
                    SendCustomEventDelayedFrames(nameof(ErrorRetry), 0);
                } else _errorRetryCount = 0;
            }
            foreach (Listener listener in _listeners) listener.OnVideoError(videoError);
        }
        #endregion
    }
}