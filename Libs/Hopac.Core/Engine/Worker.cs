﻿// Copyright (C) by Housemarque, Inc.

namespace Hopac.Core {
  using System;
  using System.Diagnostics;
  using System.Runtime.CompilerServices;
  using System.Threading;

  internal class WorkerEvent : ManualResetEventSlim {
    internal int Next;
    internal int Me;

    internal WorkerEvent(int me) {
      this.Next = -1;
      this.Me = me;
    }
  }
  
  unsafe internal struct Worker {
    internal Work WorkStack;
    internal Handler Handler;
#if TRAMPOLINE
    internal void *StackLimit;
#endif
#if ENABLE_MCS
    internal SpinlockMCS.Node Node;
#endif
    internal Scheduler Scheduler;

#if TRAMPOLINE
    [MethodImpl(AggressiveInlining.Flag)]
    internal void Init(Scheduler sr, void *StackLimit, int bytes) {
      this.StackLimit = (byte *)StackLimit - bytes;
#else
    [MethodImpl(AggressiveInlining.Flag)]
    internal void Init(Scheduler sr) {
#endif
#if ENABLE_MCS
      Node.Init();
#endif
      this.Scheduler = sr;
    }

    [MethodImpl(AggressiveInlining.Flag)]
    internal static void Push(ref Worker wr, Work work) {
      work.Next = null;
      PushNew(ref wr, work, work);
    }

    [MethodImpl(AggressiveInlining.Flag)]
    internal static void Push(ref Worker wr, Work work, Work last) {
      last.Next = null;
      PushNew(ref wr, work, last);
    }

    [MethodImpl(AggressiveInlining.Flag)]
    internal static void PushNew(ref Worker wr, Work work) {
      PushNew(ref wr, work, work);
    }

    [MethodImpl(AggressiveInlining.Flag)]
    internal static void PushNew(ref Worker wr, Work work, Work last) {
      Debug.Assert(null == last.Next);
      var older = wr.WorkStack;
      wr.WorkStack = work;
      if (null != older) {
        var sr = wr.Scheduler;
        if (null == sr.WorkStack) {
          Scheduler.PushAll(sr, older);
        } else {
          last.Next = older;
        }
      }
    }

    [MethodImpl(AggressiveInlining.Flag)]
    internal static void RunOnThisThread(Scheduler sr, Work work) {
      var wr = new Worker();
#if TRAMPOLINE
      wr.Init(sr, &wr.StackLimit, 1000);
#else
      wr.Init(sr);
#endif
      try {
        wr.Handler = work;
        work.DoWork(ref wr);
      } catch (Exception e) {
        wr.WorkStack = new FailWork(wr.WorkStack, e, wr.Handler);
      }

      Scheduler.PushAll(sr, wr.WorkStack);
    }

    [MethodImpl(AggressiveInlining.Flag)]
    internal static void RunOnThisThread<T>(Scheduler sr, Job<T> tJ, Cont<T> tK) {
      var wr = new Worker();
#if TRAMPOLINE
      wr.Init(sr, &wr.StackLimit, 1000);
#else
      wr.Init(sr);
#endif
      try {
        wr.Handler = tK;
        tJ.DoJob(ref wr, tK);
      } catch (Exception e) {
        wr.WorkStack = new FailWork(wr.WorkStack, e, wr.Handler);
      }

      Scheduler.PushAll(sr, wr.WorkStack);
    }
    
    internal static void Run(Scheduler sr, int me) {
      var wr = new Worker();
#if TRAMPOLINE
      wr.Init(sr, &wr.StackLimit, 4000);
#else
      wr.Init(sr);
#endif
      var iK = new IdleCont();

      var mine = sr.Events[me];

      while (null != sr) {
        try {
        Restart:
          Work work = wr.WorkStack;
          if (null == work)
            goto EnterScheduler;

        WorkerLoop:
          wr.WorkStack = work.Next;
          wr.Handler = work;
          work.DoWork(ref wr);
          work = wr.WorkStack;
          if (null != work)
            goto WorkerLoop;

          wr.Handler = null;

        EnterScheduler:
          work = sr.WorkStack;
          if (null == work)
            goto TryIdle;

          Scheduler.Enter(sr);

          work = sr.WorkStack;
          if (null == work)
            goto ExitAndTryIdle;

        SchedulerGotSome: {
            var last = work;
            int numWorkStack = sr.NumWorkStack - 1;
            int n = sr.NumWorkStack >> 2;
            numWorkStack -= n;
            while (n > 0) {
              last = last.Next;
              n -= 1;
            }
            var next = last.Next;
            last.Next = null;
            sr.WorkStack = next;
            if (null != next)
              Scheduler.UnsafeSignal(sr);
            sr.NumWorkStack = numWorkStack;
            Scheduler.Exit(sr);
            goto WorkerLoop;
          }

        ExitAndTryIdle:
          Scheduler.Exit(sr);

        TryIdle:
          iK.Value = Timeout.Infinite;

          var iJ = sr.IdleHandler;
          if (null != iJ) {
            wr.Handler = iK;
            iJ.DoJob(ref wr, iK);
          }

          if (0 == iK.Value)
            goto Restart;

          Scheduler.Enter(sr);
          work = sr.WorkStack;
          if (null != work)
            goto SchedulerGotSome;

          Scheduler.UnsafeWait(sr, iK.Value, mine);
          goto EnterScheduler;
        } catch (ThreadAbortException) {
          Scheduler.Signal(sr);
          sr = null;
        } catch (Exception e) {
          wr.WorkStack = new FailWork(wr.WorkStack, e, wr.Handler);
        }
      }
    }

    internal class IdleCont : Cont<int> {
      internal override void DoHandle(ref Worker wr, Exception e) {
        Handler.DoHandle(null, ref wr, e);
      }

      internal override void DoWork(ref Worker wr) { }

      internal override void DoCont(ref Worker wr, int value) {
        this.Value = value;
      }
    }
  }
}
