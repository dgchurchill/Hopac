Programming in Hopac
====================

Hopac provides a programming model that is heavily inspired by [John
Reppy](http://people.cs.uchicago.edu/~jhr/)'s *Concurrent ML* language.  The
book [Concurrent Programming in
ML](http://www.cambridge.org/us/academic/subjects/computer-science/distributed-networked-and-mobile-computing/concurrent-programming-ml)
is the most comprehensive introduction to Concurrent ML style programming.  This
document contains some discussion and examples on Hopac programming techniques.
In the future, this document might grow to a proper introduction to Hopac.

The Hopac Programming Model
---------------------------

There are two central aspects of Hopac that shape the programming model.

The first aspect is that, threads, which are called *jobs*, in Hopac are
extremely lightweight.  On modern machines you can spawn tens of millions of new
jobs in a second.  Because a job takes only a very small amount of memory,
starting from tens of bytes, a program may have millions of jobs on a modern
machine at any moment.  (Of course, at any moment, most of those jobs are
suspended, because modern machines still only have a few, or at most a few
dozen, processor cores.)  When programming in Hopac, one can therefore start new
jobs in situations where it would simply be unthinkable when using heavyweight
threads.

The other aspect is that Hopac provides first-class, higher-order, selective,
synchronous, lightweight, message passing primitives in the form of channels
(Ch) and alternatives (Alt) for coordinating and communicating between jobs.
That is a mouthful!  Let's open it up a bit.

* **First-class** means that channels and alternatives are ordinary values.
  They can be bound to variables, passed to and returned from functions and can
  even be sent from one job to another.
* **Higher-order** means that primitive alternatives can be combined and
  extended with user defined procedures to build more complex alternatives.
* **Selective** means that a form of choice or disjunction between alternatives
  is supported.  An alternative can be constructed that, for example, offers to
  give a message to another job *or* take a message from another job.  The
  choice of which operation is performed then depends on whichever alternative
  becomes available at runtime.
* **Synchronous** means that rather than building up a queue of messages for
  another job to examine, jobs can communicate via rendezvous.  Two jobs can
  meet so that one job can give a message to another job that takes the message.
* **Lightweight** means that creating a new synchronous channel takes very
  little time (a single memory allocation) and a channel takes very little
  memory on its own.

What this all boils down to is that Hopac basically provides a kind of language
for expressing concurrent control flow.

On Memory Usage
---------------

An important property of Hopac jobs and synchronous channels is that a system
that consist of **m** jobs that communicate with each other using **n**
synchronous channels (and no other primitives) requires **Theta(m + n)** space
for the jobs and channels.

That may sound obvious, but many concurrent systems,
e.g. [Erlang](http://www.erlang.org/) and F#'s
[MailboxProcessor](http://msdn.microsoft.com/en-us/library/ee370357.aspx), are
built upon asynchronous message passing primitives and in such systems message
queues can collect arbitrary numbers of messages when there are differences in
speed between producer and consumer threads.  Synchronous channels do not work
like that.  A synchronous channel doesn't hold a buffer of messages.  When a
producer job tries to give a message to a consumer job via synchronous channels,
the producer is suspended until a consumer job is ready to take the message.  A
synchronous channel provides something that is much more like a control flow
mechanism, like a procedure call, rather than a passive buffer for passing data
between threads.  This property can make it easier to understand the behaviour
of concurrent programs.

Of course, the bound **Theta(m + n)** does not take into account space that the
jobs otherwise accumulate in the form of data structures other than the
synchronous channels.

### Garbage Collection

Another aspect that is important to understand is that Hopac jobs and channels
are basic simple .Net objects and can be garbage collected.  Specifically, jobs
and channels do not inherently hold onto disposable system resources.  (This is
unlike the
[MailboxProcessor](http://msdn.microsoft.com/en-us/library/ee370357.aspx), for
example, which is disposable.)  What this means in practise is that most jobs do
not necessarily need to implement any special kill protocol.  A job that is
blocked waiting for communication on a channel that is no longer reachable can
(and will) be garbage collected.  Only jobs that explicitly hold onto some
resource that needs to be disposed must implement a kill protocol to explicitly
make sure that the resource gets properly disposed.

Example: Updatable Storage Cells
--------------------------------

In the book [Concurrent Programming in
ML](http://www.cambridge.org/us/academic/subjects/computer-science/distributed-networked-and-mobile-computing/concurrent-programming-ml),
[John Reppy](http://people.cs.uchicago.edu/~jhr/) presents as the first
programming example an implementation of updatable storage cells using
Concurrent ML channels and threads.  While this example is not exactly something
that one would do in practise, because F# lready provides ref cells, it does a
fairly nice job of illustrating some core aspects of Concurrent ML.  So, let's
reproduce the same example with Hopac.

Here is the signature for our updatable storage cells:

```fsharp
type Cell<'a>
val cell: 'a -> Job<Cell<'a>>
val get: Cell<'a> -> Job<'a>
val put: Cell<'a> -> 'a -> Job<unit>
```

The **cell** function creates a job that creates a new storage cell.  The
**get** function creates a job that returns the contents of the cell and the
**put** function creates a job that updates the contents of the cell.

The basic idea behind the implementation is that the cell is a concurrent
*server* that responds to **Get** and **Put** request.  We represent the
requests using the **Request** discriminated union type:

```fsharp
type Request<'a> =
 | Get
 | Put of 'a
```

To communicate with the outside world, the server presents two channels: one
channel for requests and another channel for replies required by the get
operation.  The **Cell** type is a record of those two channels:

```fsharp
type Cell<'a> = {
  reqCh: Ch<Request<'a>>
  replyCh: Ch<'a>
}
```

The **put** operation simply gives the **Put** request to the server via the
request channel:

```fsharp
let put (c: Cell<'a>) (x: 'a) : Job<unit> =
  Ch.give c.reqCh (Put x)
```

The **get** operation gives the **Get** request to the server via the request
channel and then takes the server's reply from the reply channel:

```fsharp
let get (c: Cell<'a>) : Job<'a> = job {
  do! Ch.give c.reqCh Get
  return! Ch.take c.replyCh
}
```

Finally, the **cell** operation actually creates the channels and starts the
concurrent server job:

```fsharp
let cell (x: 'a) : Job<Cell<'a>> = job {
  let reqCh = Ch.Now.create ()
  let replyCh = Ch.Now.create ()
  let rec server x = job {
        let! req = Ch.take reqCh
        match req with
         | Get ->
           do! Ch.give replyCh x
           return! server x
         | Put x ->
           return! server x
      }
  do! Job.start (server x)
  return {reqCh = reqCh; replyCh = replyCh}
}
```

The concurrent server is a job that loops indefinitely taking requests from the
request channel.  When the server receives a **Get** request, it gives the
current value of the cell on the reply channel and then loops to take another
request.  When the server receives a **Put** request, the server loops with the
new value to take another request.

Here is sample output of an interactive session using a cell: 

```fsharp
> let c = run (cell 1) ;;
val c : Cell<int> = ...
> run (get c) ;;
val it : int = 1
> run (put c 2) ;;
val it : unit = ()
> run (get c) ;;
val it : int = 2
```

Inspired by this example there is benchmark program, named
[Cell](https://github.com/VesaKarvonen/Hopac/tree/master/Benchmarks/Cell), that
creates large numbers of cells and large numbers of jobs running in parallel
that perform updates on randomly chosen cells.  While the benchmark program is
not terribly exciting, it nicely substantiates the claims made in the first
section about the lightweight nature of Hopac jobs and channels.

Example: Kismet
---------------



