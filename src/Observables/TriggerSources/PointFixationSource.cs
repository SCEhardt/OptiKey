﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Windows;
using JuliusSweetland.OptiKey.Extensions;
using JuliusSweetland.OptiKey.Models;

namespace JuliusSweetland.OptiKey.Observables.TriggerSources
{
    public class PointFixationSource : IFixationTriggerSource
    {
        #region Fields

        private readonly TimeSpan lockOnTime;
        private readonly TimeSpan timeToCompleteTrigger;
        private readonly TimeSpan incompleteFixationTtl;
        private readonly double fixationRadiusSquared;
        private readonly IObservable<Timestamped<PointAndKeyValue?>> pointAndKeyValueSource;
        
        private IObservable<TriggerSignal> sequence;
        private Tuple<Point, long> incompleteFixationProgress;

        #endregion

        #region Ctor

        public PointFixationSource(
            TimeSpan lockOnTime,
            TimeSpan timeToCompleteTrigger,
            TimeSpan incompleteFixationTtl,
            double fixationRadius,
            IObservable<Timestamped<PointAndKeyValue?>> pointAndKeyValueSource)
        {
            this.lockOnTime = lockOnTime;
            this.timeToCompleteTrigger = timeToCompleteTrigger;
            this.incompleteFixationTtl = incompleteFixationTtl;
            this.fixationRadiusSquared = fixationRadius * fixationRadius;
            this.pointAndKeyValueSource = pointAndKeyValueSource;
        }

        #endregion

        #region Properties

        public KeyEnabledStates KeyEnabledStates { get; set; } //Irrelevent on point trigger

        public IObservable<TriggerSignal> Sequence
        {
            get
            {
                if (sequence == null)
                { 
                    sequence = Observable.Create<TriggerSignal>(observer =>
                    {
                        bool disposed = false;

                        var buffer = new List<Timestamped<PointAndKeyValue>>();
                        DateTimeOffset fixationStart = DateTimeOffset.MinValue;
                        PointAndKeyValue? fixationCentrePointAndKeyValue = null;

                        Action disposeAllSubscriptions = null;
                        
                        var pointAndKeyValueSubscription = pointAndKeyValueSource
                            .Where(_ => disposed == false)
                            .Where(tp => tp.Value != null) //Filter out stale indicators - the fixation progress is not reset by the points sequence being stale
                            .Select(tp => new Timestamped<PointAndKeyValue>(tp.Value.Value, tp.Timestamp))
                            .Subscribe(point =>
                            {
                                //Maintain a buffer which contains points which fill the lockOnTime 
                                buffer.Add(new Timestamped<PointAndKeyValue>(point.Value, point.Timestamp));
                                var lockOnStartTime = point.Timestamp.Subtract(lockOnTime);
                                var bufferFullFromIndex = buffer.FindLastIndex(tpakv => tpakv.Timestamp < lockOnStartTime);
                                bool bufferIsFull = bufferFullFromIndex > -1;
                                if (bufferFullFromIndex > 0) //Trim the buffer - only keep lock-on period + 1 point earlier than limit
                                {
                                    buffer.RemoveRange(0, bufferFullFromIndex);
                                }
                                
                                if (fixationCentrePointAndKeyValue == null)
                                {
                                    //No fixation in progress - we are in the "lock on" phase
                                    if (bufferIsFull)
                                    {
                                        //Test if all buffered points (enough for a lock-on) are within an acceptable radius of their centre point
                                        var centrePoint = buffer.Select(t => t.Value.Point).ToList().CalculateCentrePoint();
                                        if (buffer.All(t => 
                                            (Math.Pow((centrePoint.X - t.Value.Point.X), 2)
                                            + Math.Pow((centrePoint.Y - t.Value.Point.Y), 2)) 
                                            < fixationRadiusSquared)) //Bit of right-angled triangle maths: a squared + b squared = c squared
                                        {
                                            //Lock-on complete - start a new fixation
                                            fixationCentrePointAndKeyValue = new PointAndKeyValue(centrePoint, null);
                                            fixationStart = point.Timestamp;

                                            if (incompleteFixationProgress != null
                                                && (Math.Pow((fixationCentrePointAndKeyValue.Value.Point.X - incompleteFixationProgress.Item1.X), 2)
                                                    + Math.Pow((fixationCentrePointAndKeyValue.Value.Point.Y - incompleteFixationProgress.Item1.Y), 2))
                                                    > fixationRadiusSquared) //Bit of right-angled triangle maths: a squared + b squared = c squared
                                            {
                                                //Existing incomplete fixation is not relevent to new fixation - clear it
                                                incompleteFixationProgress = null;
                                            }
                                        }
                                    }
                                }
                                else
                                {
                                    //We have a current fixation
                                    if ((Math.Pow((fixationCentrePointAndKeyValue.Value.Point.X - point.Value.Point.X), 2)
                                        + Math.Pow((fixationCentrePointAndKeyValue.Value.Point.Y - point.Value.Point.Y), 2)) 
                                        > fixationRadiusSquared) //Bit of right-angled triangle maths: a squared + b squared = c squared
                                    {
                                        //Latest point is outside the acceptable radius of the current fixation
                                        if (buffer.Count > 1)
                                        {
                                            //We have a buffer which contains the previous (within acceptable radius) point
                                            Timestamped<PointAndKeyValue>? previousPointAndKeyValue = buffer[buffer.Count - 2];

                                            //Calculate the span of the fixation up to this point and store the aggregate progress (so that we can resume progress later)
                                            var fixationSpan = previousPointAndKeyValue.Value.Timestamp.Subtract(fixationStart);
                                            long previouslyStoredProgress = GetIncompleteFixationProgressForPoint(fixationCentrePointAndKeyValue.Value.Point);
                                            incompleteFixationProgress = new Tuple<Point, long>(
                                                fixationCentrePointAndKeyValue.Value.Point, previouslyStoredProgress + fixationSpan.Ticks);
                                        }
                                        
                                        //Clear the current fixation
                                        fixationCentrePointAndKeyValue = null;
                                    }
                                    else
                                    {
                                        //We have created or added to a fixation - update state vars, publish our progress and reset if necessary
                                        var fixationSpan = point.Timestamp.Subtract(fixationStart);
                                        var storedProgress = GetIncompleteFixationProgressForPoint(fixationCentrePointAndKeyValue.Value.Point);
                                        var progress = (((double)(storedProgress + fixationSpan.Ticks)) / (double)timeToCompleteTrigger.Ticks);

                                        //Publish a high signal if progress is 1 (100%), otherwise just publish progress (filter out 0 as this is a progress reset signal)
                                        if (progress > 0)
                                        {
                                            observer.OnNext(new TriggerSignal(
                                                progress >= 1 ? 1 : (double?)null, progress >= 1 ? 1 : progress, fixationCentrePointAndKeyValue));
                                        }

                                        //Reset if we've just published a high signal
                                        if (progress >= 1)
                                        {
                                            fixationCentrePointAndKeyValue = null;
                                            incompleteFixationProgress = null;
                                            buffer.Clear();
                                            observer.OnNext(new TriggerSignal(null, 0, null));
                                            return;
                                        }
                                    }
                                }
                            },
                            ex =>
                            {
                                observer.OnError(ex);
                                disposeAllSubscriptions();
                            });

                        disposeAllSubscriptions = () =>
                        {
                            disposed = true;

                            if (pointAndKeyValueSubscription != null)
                            {
                                pointAndKeyValueSubscription.Dispose();
                                pointAndKeyValueSubscription = null;
                            }
                        };

                        return disposeAllSubscriptions;
                    })
                    .Publish()
                    .RefCount();
                }

                return sequence;
            }
        }

        #endregion

        #region Private methods

        private long GetIncompleteFixationProgressForPoint(Point point)
        {
            if (incompleteFixationProgress != null
                && ((Math.Pow((incompleteFixationProgress.Item1.X - point.X), 2)
                    + Math.Pow((incompleteFixationProgress.Item1.Y - point.Y), 2))
                    < fixationRadiusSquared)) //Bit of right-angled triangle maths: a squared + b squared = c squared
            {
                //The fixation is within the acceptable radius of an incomplete fixation - get previous progress
                return incompleteFixationProgress.Item2;
            }

            return 0;
        }

        #endregion
    }
}
