using System;
using System.Collections.Generic;
using Prim.Serialization;
using Xunit;

namespace Prim.Tests.Unit
{
    /// <summary>
    /// TDD tests for ObjectGraphTracker edge cases.
    /// Targets the unsafe dictionary access in GetId (line 56) and
    /// various boundary conditions in registration/retrieval.
    /// </summary>
    public class ObjectGraphTrackerEdgeCaseTests
    {
        #region GetId - Unsafe Dictionary Access Bug

        [Fact]
        public void GetId_ThrowsKeyNotFoundException_ForUnregisteredObject()
        {
            // BUG: GetId uses direct dictionary [] access (line 56)
            // which throws KeyNotFoundException for unregistered objects
            // instead of returning a sentinel value or using TryGetValue.
            var tracker = new ObjectGraphTracker();
            var unregistered = new object();

            Assert.Throws<KeyNotFoundException>(() => tracker.GetId(unregistered));
        }

        [Fact]
        public void GetId_ReturnsNegativeOne_ForNull()
        {
            var tracker = new ObjectGraphTracker();

            var id = tracker.GetId(null);

            Assert.Equal(-1, id);
        }

        [Fact]
        public void GetId_ReturnsCorrectId_ForRegisteredObject()
        {
            var tracker = new ObjectGraphTracker();
            var obj = new object();

            tracker.TryRegister(obj, out var registeredId);
            var retrievedId = tracker.GetId(obj);

            Assert.Equal(registeredId, retrievedId);
        }

        #endregion

        #region TryRegister - Null Handling Semantics

        [Fact]
        public void TryRegister_Null_AlwaysReturnsTrue_WithIdNegativeOne()
        {
            // Null is treated as a trackable registration with id = -1.
            // First registration returns true, subsequent registrations return false
            // (consistent with non-null behavior).
            var tracker = new ObjectGraphTracker();

            Assert.True(tracker.TryRegister(null, out var id1));
            Assert.Equal(-1, id1);

            // Second registration of null returns false (already tracked)
            Assert.False(tracker.TryRegister(null, out var id2));
            Assert.Equal(-1, id2);
        }

        [Fact]
        public void TryRegister_Null_DoesNotIncrementNextId()
        {
            var tracker = new ObjectGraphTracker();

            tracker.TryRegister(null, out _);
            tracker.TryRegister(new object(), out var id);

            // The null registration shouldn't consume an ID
            Assert.Equal(0, id);
        }

        [Fact]
        public void TryRegister_AssignsSequentialIds()
        {
            var tracker = new ObjectGraphTracker();
            var objects = new[] { new object(), new object(), new object() };

            for (int i = 0; i < objects.Length; i++)
            {
                Assert.True(tracker.TryRegister(objects[i], out var id));
                Assert.Equal(i, id);
            }
        }

        [Fact]
        public void TryRegister_SameObject_ReturnsFalse_WithSameId()
        {
            var tracker = new ObjectGraphTracker();
            var obj = new object();

            Assert.True(tracker.TryRegister(obj, out var firstId));
            Assert.False(tracker.TryRegister(obj, out var secondId));
            Assert.Equal(firstId, secondId);
        }

        #endregion

        #region IsRegistered - Edge Cases

        [Fact]
        public void IsRegistered_ReturnsFalse_ForNull()
        {
            var tracker = new ObjectGraphTracker();

            // Even after "registering" null, IsRegistered returns false
            // because null is explicitly handled to return false
            tracker.TryRegister(null, out _);
            Assert.False(tracker.IsRegistered(null));
        }

        [Fact]
        public void IsRegistered_ReturnsFalse_ForUnregisteredObject()
        {
            var tracker = new ObjectGraphTracker();

            Assert.False(tracker.IsRegistered(new object()));
        }

        [Fact]
        public void IsRegistered_ReturnsTrue_AfterTryRegister()
        {
            var tracker = new ObjectGraphTracker();
            var obj = new object();

            tracker.TryRegister(obj, out _);

            Assert.True(tracker.IsRegistered(obj));
        }

        #endregion

        #region RegisterDeserialized - Edge Cases

        [Fact]
        public void RegisterDeserialized_ThrowsForNegativeId()
        {
            var tracker = new ObjectGraphTracker();

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                tracker.RegisterDeserialized(-1, new object()));
        }

        [Fact]
        public void RegisterDeserialized_ExpandsListForGapIds()
        {
            // If we register id=5 without 0-4, the list should be expanded with nulls
            var tracker = new ObjectGraphTracker();
            var obj = new object();

            tracker.RegisterDeserialized(5, obj);

            // IDs 0-4 should return null
            for (int i = 0; i < 5; i++)
            {
                Assert.Null(tracker.GetById(i));
            }
            Assert.Same(obj, tracker.GetById(5));
        }

        [Fact]
        public void RegisterDeserialized_ThrowsForConflictingRegistration()
        {
            var tracker = new ObjectGraphTracker();
            var obj1 = new object();
            var obj2 = new object();

            tracker.RegisterDeserialized(0, obj1);

            // Registering a different object to the same ID should throw
            Assert.Throws<InvalidOperationException>(() =>
                tracker.RegisterDeserialized(0, obj2));
        }

        [Fact]
        public void RegisterDeserialized_AllowsReregistrationOfSameObject()
        {
            var tracker = new ObjectGraphTracker();
            var obj = new object();

            tracker.RegisterDeserialized(0, obj);
            // Re-registering the same object to the same ID should not throw
            tracker.RegisterDeserialized(0, obj);

            Assert.Same(obj, tracker.GetById(0));
        }

        #endregion

        #region GetById - Boundary Conditions

        [Fact]
        public void GetById_ReturnsNull_ForNegativeId()
        {
            var tracker = new ObjectGraphTracker();

            Assert.Null(tracker.GetById(-1));
            Assert.Null(tracker.GetById(-100));
        }

        [Fact]
        public void GetById_ReturnsNull_ForOutOfRangeId()
        {
            var tracker = new ObjectGraphTracker();

            Assert.Null(tracker.GetById(0));
            Assert.Null(tracker.GetById(999));
        }

        [Fact]
        public void GetById_ReturnsNull_ForUnfilledSlots()
        {
            var tracker = new ObjectGraphTracker();
            tracker.RegisterDeserialized(3, new object());

            Assert.Null(tracker.GetById(0));
            Assert.Null(tracker.GetById(1));
            Assert.Null(tracker.GetById(2));
        }

        #endregion

        #region Clear - State Reset

        [Fact]
        public void Clear_ResetsAllState()
        {
            var tracker = new ObjectGraphTracker();
            var obj = new object();

            tracker.TryRegister(obj, out _);
            tracker.RegisterDeserialized(0, obj);

            tracker.Clear();

            Assert.False(tracker.IsRegistered(obj));
            Assert.Null(tracker.GetById(0));
        }

        [Fact]
        public void Clear_AllowsReuse_WithFreshIds()
        {
            var tracker = new ObjectGraphTracker();
            var obj1 = new object();

            tracker.TryRegister(obj1, out var id1);
            Assert.Equal(0, id1);

            tracker.Clear();

            var obj2 = new object();
            tracker.TryRegister(obj2, out var id2);
            // After clear, IDs should restart from 0
            Assert.Equal(0, id2);
        }

        #endregion

        #region Reference Equality Semantics

        [Fact]
        public void TryRegister_UsesReferenceEquality_NotValueEquality()
        {
            var tracker = new ObjectGraphTracker();

            // Two strings with same value but different references (force no interning)
            var str1 = new string('a', 5);
            var str2 = new string('a', 5);

            Assert.Equal(str1, str2); // Value equal
            Assert.False(ReferenceEquals(str1, str2)); // Reference different

            Assert.True(tracker.TryRegister(str1, out var id1));
            Assert.True(tracker.TryRegister(str2, out var id2));
            Assert.NotEqual(id1, id2);
        }

        [Fact]
        public void IsRegistered_UsesReferenceEquality()
        {
            var tracker = new ObjectGraphTracker();
            var str1 = new string('a', 5);
            var str2 = new string('a', 5);

            tracker.TryRegister(str1, out _);

            Assert.True(tracker.IsRegistered(str1));
            Assert.False(tracker.IsRegistered(str2));
        }

        #endregion
    }
}
