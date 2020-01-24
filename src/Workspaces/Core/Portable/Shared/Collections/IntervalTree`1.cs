﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.CodeAnalysis.PooledObjects;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.Shared.Collections
{
    /// <summary>
    /// An interval tree represents an ordered tree data structure to store intervals of the form 
    /// [start, end).  It allows you to efficiently find all intervals that intersect or overlap 
    /// a provided interval.
    /// </summary>
    internal partial class IntervalTree<T, TIntrospector> : IEnumerable<T>
        where TIntrospector : struct, IIntervalIntrospector<T>
    {
        public static readonly IntervalTree<T, TIntrospector> Empty = new IntervalTree<T, TIntrospector>();

        protected Node root;

        private delegate bool TestInterval(T value, int start, int length, in TIntrospector introspector);
        private static readonly TestInterval s_intersectsWithTest = IntersectsWith;
        private static readonly TestInterval s_containsTest = Contains;
        private static readonly TestInterval s_overlapsWithTest = OverlapsWith;

        private static readonly ObjectPool<Stack<(Node node, bool firstTime)>> s_stackPool
            = SharedPools.Default<Stack<(Node node, bool firstTime)>>();

        public IntervalTree()
        {
        }

        public IntervalTree(in TIntrospector introspector, IEnumerable<T> values)
        {
            foreach (var value in values)
            {
                root = Insert(root, new Node(value), in introspector);
            }
        }

        protected static bool Contains(T value, int start, int length, in TIntrospector introspector)
        {
            var otherStart = start;
            var otherEnd = start + length;

            var thisEnd = GetEnd(value, in introspector);
            var thisStart = introspector.GetStart(value);

            // make sure "Contains" test to be same as what TextSpan does
            if (length == 0)
            {
                return thisStart <= otherStart && otherEnd < thisEnd;
            }

            return thisStart <= otherStart && otherEnd <= thisEnd;
        }

        private static bool IntersectsWith(T value, int start, int length, in TIntrospector introspector)
        {
            var otherStart = start;
            var otherEnd = start + length;

            var thisEnd = GetEnd(value, in introspector);
            var thisStart = introspector.GetStart(value);

            return otherStart <= thisEnd && otherEnd >= thisStart;
        }

        private static bool OverlapsWith(T value, int start, int length, in TIntrospector introspector)
        {
            var otherStart = start;
            var otherEnd = start + length;

            var thisEnd = GetEnd(value, in introspector);
            var thisStart = introspector.GetStart(value);

            if (length == 0)
            {
                return thisStart < otherStart && otherStart < thisEnd;
            }

            var overlapStart = Math.Max(thisStart, otherStart);
            var overlapEnd = Math.Min(thisEnd, otherEnd);

            return overlapStart < overlapEnd;
        }

        public ImmutableArray<T> GetIntervalsThatOverlapWith(int start, int length, in TIntrospector introspector)
            => this.GetIntervalsThatMatch(start, length, s_overlapsWithTest, in introspector);

        public ImmutableArray<T> GetIntervalsThatIntersectWith(int start, int length, in TIntrospector introspector)
            => this.GetIntervalsThatMatch(start, length, s_intersectsWithTest, in introspector);

        public ImmutableArray<T> GetIntervalsThatContain(int start, int length, in TIntrospector introspector)
            => this.GetIntervalsThatMatch(start, length, s_containsTest, in introspector);

        public void FillWithIntervalsThatOverlapWith(int start, int length, ArrayBuilder<T> builder, in TIntrospector introspector)
            => this.FillWithIntervalsThatMatch(start, length, s_overlapsWithTest, builder, in introspector, stopAfterFirst: false);

        public void FillWithIntervalsThatIntersectWith(int start, int length, ArrayBuilder<T> builder, in TIntrospector introspector)
            => this.FillWithIntervalsThatMatch(start, length, s_intersectsWithTest, builder, in introspector, stopAfterFirst: false);

        public void FillWithIntervalsThatContain(int start, int length, ArrayBuilder<T> builder, in TIntrospector introspector)
            => this.FillWithIntervalsThatMatch(start, length, s_containsTest, builder, in introspector, stopAfterFirst: false);

        public bool HasIntervalThatIntersectsWith(int position, in TIntrospector introspector)
            => HasIntervalThatIntersectsWith(position, 0, in introspector);

        public bool HasIntervalThatIntersectsWith(int start, int length, in TIntrospector introspector)
            => Any(start, length, s_intersectsWithTest, in introspector);

        public bool HasIntervalThatOverlapsWith(int start, int length, in TIntrospector introspector)
            => Any(start, length, s_overlapsWithTest, in introspector);

        public bool HasIntervalThatContains(int start, int length, in TIntrospector introspector)
            => Any(start, length, s_containsTest, in introspector);

        private bool Any(int start, int length, TestInterval testInterval, in TIntrospector introspector)
        {
            var builder = ArrayBuilder<T>.GetInstance();
            FillWithIntervalsThatMatch(start, length, testInterval, builder, in introspector, stopAfterFirst: true);

            var result = builder.Count > 0;
            builder.Free();
            return result;
        }

        private ImmutableArray<T> GetIntervalsThatMatch(
            int start, int length, TestInterval testInterval, in TIntrospector introspector)
        {
            var result = ArrayBuilder<T>.GetInstance();
            FillWithIntervalsThatMatch(start, length, testInterval, result, in introspector, stopAfterFirst: false);
            return result.ToImmutableAndFree();
        }

        private void FillWithIntervalsThatMatch(
            int start, int length, TestInterval testInterval,
            ArrayBuilder<T> builder, in TIntrospector introspector,
            bool stopAfterFirst)
        {
            if (root == null)
            {
                return;
            }

            var candidates = s_stackPool.Allocate();

            FillWithIntervalsThatMatch(
                start, length, testInterval,
                builder, in introspector,
                stopAfterFirst, candidates);

            s_stackPool.ClearAndFree(candidates);
        }

        private void FillWithIntervalsThatMatch(
            int start, int length, TestInterval testInterval,
            ArrayBuilder<T> builder, in TIntrospector introspector,
            bool stopAfterFirst, Stack<(Node node, bool firstTime)> candidates)
        {
            var end = start + length;

            candidates.Push((root, firstTime: true));

            while (candidates.Count > 0)
            {
                var currentTuple = candidates.Pop();
                var currentNode = currentTuple.node;
                Debug.Assert(currentNode != null);

                var firstTime = currentTuple.firstTime;

                if (!firstTime)
                {
                    // We're seeing this node for the second time (as we walk back up the left
                    // side of it).  Now see if it matches our test, and if so return it out.
                    if (testInterval(currentNode.Value, start, length, in introspector))
                    {
                        builder.Add(currentNode.Value);

                        if (stopAfterFirst)
                        {
                            return;
                        }
                    }
                }
                else
                {
                    // First time we're seeing this node.  In order to see the node 'in-order',
                    // we push the right side, then the node again, then the left side.  This 
                    // time we mark the current node with 'false' to indicate that it's the
                    // second time we're seeing it the next time it comes around.

                    // right children's starts will never be to the left of the parent's start
                    // so we should consider right subtree only if root's start overlaps with
                    // interval's End, 
                    if (introspector.GetStart(currentNode.Value) <= end)
                    {
                        var right = currentNode.Right;
                        if (right != null && GetEnd(right.MaxEndNode.Value, in introspector) >= start)
                        {
                            candidates.Push((right, firstTime: true));
                        }
                    }

                    candidates.Push((currentNode, firstTime: false));

                    // only if left's maxVal overlaps with interval's start, we should consider 
                    // left subtree
                    var left = currentNode.Left;
                    if (left != null && GetEnd(left.MaxEndNode.Value, in introspector) >= start)
                    {
                        candidates.Push((left, firstTime: true));
                    }
                }
            }
        }

        public bool IsEmpty() => this.root == null;

        protected static Node Insert(Node root, Node newNode, in TIntrospector introspector)
        {
            var newNodeStart = introspector.GetStart(newNode.Value);
            return Insert(root, newNode, newNodeStart, in introspector);
        }

        private static Node Insert(Node root, Node newNode, int newNodeStart, in TIntrospector introspector)
        {
            if (root == null)
            {
                return newNode;
            }

            Node newLeft, newRight;

            if (newNodeStart < introspector.GetStart(root.Value))
            {
                newLeft = Insert(root.Left, newNode, newNodeStart, in introspector);
                newRight = root.Right;
            }
            else
            {
                newLeft = root.Left;
                newRight = Insert(root.Right, newNode, newNodeStart, in introspector);
            }

            root.SetLeftRight(newLeft, newRight, in introspector);
            var newRoot = root;

            return Balance(newRoot, in introspector);
        }

        private static Node Balance(Node node, in TIntrospector introspector)
        {
            var balanceFactor = BalanceFactor(node);
            if (balanceFactor == -2)
            {
                var rightBalance = BalanceFactor(node.Right);
                if (rightBalance == -1)
                {
                    return node.LeftRotation(in introspector);
                }
                else
                {
                    Debug.Assert(rightBalance == 1);
                    return node.InnerRightOuterLeftRotation(in introspector);
                }
            }
            else if (balanceFactor == 2)
            {
                var leftBalance = BalanceFactor(node.Left);
                if (leftBalance == 1)
                {
                    return node.RightRotation(in introspector);
                }
                else
                {
                    Debug.Assert(leftBalance == -1);
                    return node.InnerLeftOuterRightRotation(in introspector);
                }
            }

            return node;
        }

        public IEnumerator<T> GetEnumerator()
        {
            if (root == null)
            {
                yield break;
            }

            var candidates = new Stack<(Node node, bool firstTime)>();
            candidates.Push((root, firstTime: true));
            while (candidates.Count != 0)
            {
                var (currentNode, firstTime) = candidates.Pop();
                if (currentNode != null)
                {
                    if (firstTime)
                    {
                        // First time seeing this node.  Mark that we've been seen and recurse
                        // down the left side.  The next time we see this node we'll yield it
                        // out.
                        candidates.Push((currentNode.Right, firstTime: true));
                        candidates.Push((currentNode, firstTime: false));
                        candidates.Push((currentNode.Left, firstTime: true));
                    }
                    else
                    {
                        yield return currentNode.Value;
                    }
                }
            }
        }

        IEnumerator IEnumerable.GetEnumerator()
            => this.GetEnumerator();

        protected static int GetEnd(T value, in TIntrospector introspector)
            => introspector.GetStart(value) + introspector.GetLength(value);

        protected static int MaxEndValue(Node node, in TIntrospector introspector)
            => node == null ? 0 : GetEnd(node.MaxEndNode.Value, in introspector);

        private static int Height(Node node)
            => node == null ? 0 : node.Height;

        private static int BalanceFactor(Node node)
            => node == null ? 0 : Height(node.Left) - Height(node.Right);
    }
}
