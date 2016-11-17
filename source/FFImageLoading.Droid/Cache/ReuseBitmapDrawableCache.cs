using System.Linq;
using System;
using Android.OS;
using Android.Graphics;
using System.Collections.Generic;
using Android.Util;
using FFImageLoading.Collections;
using FFImageLoading.Helpers;
using FFImageLoading.Drawables;

namespace FFImageLoading.Cache
{
	public class ReuseBitmapDrawableCache : IDictionary<string, ISelfDisposingBitmapDrawable>
	{
        readonly object monitor = new object();
		const string TAG = "ReuseBitmapDrawableCache";

		int total_added;
		int total_removed;
		int total_reuse_hits;
		int total_reuse_misses;
		int total_evictions;
		int total_cache_hits;
		long current_cache_byte_count;

		readonly long high_watermark;
		readonly long low_watermark;
		bool reuse_pool_refill_needed = true;

		/// <summary>
		/// Contains all entries that are currently being displayed. These entries are not eligible for
		/// reuse or eviction. Entries will be added to the reuse pool when they are no longer displayed.
		/// </summary>
		IDictionary<string, ISelfDisposingBitmapDrawable> displayed_cache;
		/// <summary>
		/// Contains entries that potentially available for reuse and candidates for eviction.
		/// This is the default location for newly added entries. This cache
		/// is searched along with the displayed cache for cache hits. If a cache hit is found here, its
		/// place in the LRU list will be refreshed. Items only move out of reuse and into displayed
		/// when the entry has SetIsDisplayed(true) called on it.
		/// </summary>
		readonly ByteBoundStrongLruCache<string, ISelfDisposingBitmapDrawable> reuse_pool;

		readonly TimeSpan debug_dump_interval = TimeSpan.FromSeconds(10);
		readonly Handler main_thread_handler;
		readonly IMiniLogger log;
        readonly bool _verboseLogging;

		/// <summary>
		/// Initializes a new instance of the <see cref="ReuseBitmapDrawableCache"/> class.
		/// </summary>
		/// <param name="logger">Logger for debug messages</param>
		/// <param name="highWatermark">Maximum number of bytes the reuse pool will hold before starting evictions.
		/// <param name="lowWatermark">Number of bytes the reuse pool will be drained down to after the high watermark is exceeded.</param> 
		/// On Honeycomb and higher this value is used for the reuse pool size.</param>
		public ReuseBitmapDrawableCache(IMiniLogger logger, long highWatermark, long lowWatermark, bool verboseLogging = false)
		{
            _verboseLogging = verboseLogging;
			log = logger;
			low_watermark = lowWatermark;
			high_watermark = highWatermark;
			displayed_cache = new Dictionary<string, ISelfDisposingBitmapDrawable>();
			reuse_pool = new ByteBoundStrongLruCache<string, ISelfDisposingBitmapDrawable>(highWatermark, lowWatermark);
			reuse_pool.EntryRemoved += OnEntryRemovedFromReusePool;
		}

		/// <summary>
		/// Attempts to find a bitmap suitable for reuse based on the given dimensions.
		/// Note that any returned instance will have SetIsRetained(true) called on it
		/// to ensure that it does not release its resources prematurely as it is leaving
		/// cache management. This means you must call SetIsRetained(false) when you no
		/// longer need the instance.
		/// </summary>
		/// <returns>A ISelfDisposingBitmapDrawable that has been retained. You must call SetIsRetained(false)
		/// when finished using it.</returns>
		/// <param name="width">Width of the image to be written to the bitmap allocation.</param>
		/// <param name="height">Height of the image to be written to the bitmap allocation.</param>
		/// <param name="inSampleSize">DownSample factor.</param>
		public ISelfDisposingBitmapDrawable GetReusableBitmapDrawable(BitmapFactory.Options options)
		{
			if (reuse_pool == null) 
                return null;

			// Only attempt to get a bitmap for reuse if the reuse cache is full.
			// This prevents us from prematurely depleting the pool and allows
			// more cache hits, as the most recently added entries will have a high
			// likelihood of being accessed again so we don't want to steal those bytes too soon.
			lock (monitor) 
            {
				if (reuse_pool.CacheSizeInBytes < low_watermark && reuse_pool_refill_needed) 
                {
					total_reuse_misses++;
					return null;
				}

				reuse_pool_refill_needed = false;
				ISelfDisposingBitmapDrawable reuseDrawable = null;

				if (reuse_pool.Count > 0) 
                {
					var reuse_keys = reuse_pool.Keys;
					foreach (var k in reuse_keys) 
                    {
						var bd = reuse_pool.Peek(k);
                        if (bd.IsValidAndHasValidBitmap() && bd.Bitmap.IsMutable && !bd.IsRetained && CanUseForInBitmap(bd.Bitmap, options))
						{
                            reuseDrawable = bd;
                            break;
						}
					}

					if (reuseDrawable != null) 
                    {
						reuseDrawable.SetIsRetained(true);
						UpdateByteUsage(reuseDrawable.Bitmap, decrement:true, causedByEviction: true);

						// Cleanup the entry
						reuseDrawable.Displayed -= OnEntryDisplayed;
						reuseDrawable.NoLongerDisplayed -= OnEntryNoLongerDisplayed;
						reuseDrawable.SetIsCached(false);
						reuse_pool.Remove(reuseDrawable.InCacheKey);
						total_reuse_hits++;
					}
				}

				if (reuseDrawable == null) 
                {
					total_reuse_misses++;
					// Indicate that the pool may need to be refilled.
					// There is little harm in setting this flag since it will be unset
					// on the next reuse request if the threshold is reuse_pool.CacheSizeInBytes >= low_watermark.
					reuse_pool_refill_needed = true;
				}
				return reuseDrawable;
			}
		}

        bool CanUseForInBitmap(Bitmap candidate, BitmapFactory.Options targetOptions)
        {
            if (Utils.HasKitKat())
            {
                // From Android 4.4 (KitKat) onward we can re-use if the byte size of
                // the new bitmap is smaller than the reusable bitmap candidate
                // allocation byte count.
                int width = targetOptions.OutWidth / targetOptions.InSampleSize;
                int height = targetOptions.OutHeight / targetOptions.InSampleSize;
                int byteCount = width * height * GetBytesPerPixel(candidate.GetConfig());
                return byteCount <= candidate.AllocationByteCount;

                //  int newWidth = (int)Math.Ceiling(width/(float)inSampleSize);
                //  int newHeight = (int)Math.Ceiling(height/(float)inSampleSize);

                //  if (inSampleSize > 1)
                //  {
                //      // Android docs: the decoder uses a final value based on powers of 2, any other value will be rounded down to the nearest power of 2.
                //      //if (newWidth % 2 != 0)
                //      //  newWidth += 1;

                //      //if (newHeight % 2 != 0)
                //      //  newHeight += 1; 
                //  }
            }

            // On earlier versions, the dimensions must match exactly and the inSampleSize must be 1
            return candidate.Width == targetOptions.OutWidth
                    && candidate.Height == targetOptions.OutHeight
                    && targetOptions.InSampleSize == 1;
        }

		/// <summary>
		/// Return the byte usage per pixel of a bitmap based on its configuration.
		/// </summary>
		/// <param name="config">The bitmap configuration</param>
		/// <returns>The byte usage per pixel</returns>
		int GetBytesPerPixel(Bitmap.Config config)
		{
			if (config == Bitmap.Config.Argb8888)
			{
				return 4;
			}
			else if (config == Bitmap.Config.Rgb565)
			{
				return 2;
			}
			else if (config == Bitmap.Config.Argb4444)
			{
				return 2;
			}
			else if (config == Bitmap.Config.Alpha8)
			{
				return 1;
			}
			return 1;
		}

		void UpdateByteUsage(Bitmap bitmap, bool decrement = false, bool causedByEviction = false)
		{
			lock(monitor) 
            {
				var byteCount = bitmap.RowBytes * bitmap.Height;
				current_cache_byte_count += byteCount * (decrement ? -1 : 1);

                // DISABLED - performance is better withut it
				//if (causedByEviction) 
                //{
				//	current_evicted_byte_count += byteCount;
				//	// Kick the gc if we've accrued more than our desired threshold.
				//	// TODO: Implement high/low watermarks to prevent thrashing
				//	if (current_evicted_byte_count > gc_threshold) {
				//		total_forced_gc_collections++;
                //        if (_verboseLogging)
				//		    log.Debug("Memory usage exceeds threshold, invoking GC.Collect");
				//		// Force immediate Garbage collection. Please note that is resource intensive.
				//		System.GC.Collect();
				//		System.GC.WaitForPendingFinalizers ();
				//		System.GC.WaitForPendingFinalizers (); // Double call since GC doesn't always find resources to be collected: https://bugzilla.xamarin.com/show_bug.cgi?id=20503
				//		System.GC.Collect ();
				//		current_evicted_byte_count = 0;
				//	}
				//}
			}
		}

		void OnEntryRemovedFromReusePool (object sender, EntryRemovedEventArgs<string, ISelfDisposingBitmapDrawable> e)
		{
            ProcessRemoval(e.Value, e.Evicted);
		}

		void ProcessRemoval(ISelfDisposingBitmapDrawable value, bool evicted)
		{
			lock(monitor) 
            {
				total_removed++;
				if (evicted) 
                {
                    if (_verboseLogging)
					    log.Debug(string.Format("Evicted key: {0}", value.InCacheKey));
					total_evictions++;
				}
			}

			// We only really care about evictions because we do direct Remove()als
			// all the time when promoting to the displayed_cache. Only when the
			// entry has been evicted is it truly not longer being held by us.
			if (evicted) 
            {
				UpdateByteUsage(value.Bitmap, decrement: true, causedByEviction: true);

				value.SetIsCached(false);
				value.Displayed -= OnEntryDisplayed;
				value.NoLongerDisplayed -= OnEntryNoLongerDisplayed;
			}
		}

		void OnEntryNoLongerDisplayed(object sender, EventArgs args)
		{
			var sdbd = sender as ISelfDisposingBitmapDrawable;

			if (sdbd != null) 
			{
				lock (monitor) 
				{
					if (displayed_cache.ContainsKey(sdbd.InCacheKey))
						DemoteDisplayedEntryToReusePool(sdbd);
				}
			}
		}

		void OnEntryDisplayed(object sender, EventArgs args)
		{
			var sdbd = sender as ISelfDisposingBitmapDrawable;

			if (sdbd != null) 
			{
				// see if the sender is in the reuse pool and move it
				// into the displayed_cache if found.
				lock (monitor) 
				{
					if (reuse_pool.ContainsKey(sdbd.InCacheKey))
						PromoteReuseEntryToDisplayedCache(sdbd);
				}
			}
		}

		void OnEntryAdded(string key, ISelfDisposingBitmapDrawable value)
		{
			total_added++;
            if (_verboseLogging)
			    log.Debug(string.Format("OnEntryAdded(key = {0})", key));
            
			var selfDisposingBitmapDrawable = value as ISelfDisposingBitmapDrawable;
			if (selfDisposingBitmapDrawable != null) 
            {
				selfDisposingBitmapDrawable.SetIsCached(true);
				selfDisposingBitmapDrawable.InCacheKey = key;
				selfDisposingBitmapDrawable.Displayed += OnEntryDisplayed;
				UpdateByteUsage(selfDisposingBitmapDrawable.Bitmap);
			}
		}

		void PromoteReuseEntryToDisplayedCache(ISelfDisposingBitmapDrawable value)
		{
			value.Displayed -= OnEntryDisplayed;
			value.NoLongerDisplayed += OnEntryNoLongerDisplayed;
			reuse_pool.Remove(value.InCacheKey);
			displayed_cache.Add(value.InCacheKey, value);
		}

		void DemoteDisplayedEntryToReusePool(ISelfDisposingBitmapDrawable value)
		{
			value.NoLongerDisplayed -= OnEntryNoLongerDisplayed;
			value.Displayed += OnEntryDisplayed;
			displayed_cache.Remove(value.InCacheKey);
			reuse_pool.Add(value.InCacheKey, value);
		}

		#region IDictionary implementation

		public void Add(string key, ISelfDisposingBitmapDrawable value)
		{
            if (string.IsNullOrEmpty(key) || value == null) 
            {
                if (_verboseLogging)
				    log.Error("Attempt to add null value, refusing to cache");
				return;
			}

			if (!value.HasValidBitmap) 
            {
                if (_verboseLogging)
				    log.Error("Attempt to add Drawable with null or recycled bitmap, refusing to cache");
				return;
			}

			lock (monitor) 
            {
				if (!displayed_cache.ContainsKey(key) && !reuse_pool.ContainsKey(key)) {
					reuse_pool.Add(key, value);
					OnEntryAdded(key, value);
				}
			}
		}

		public bool ContainsKey(string key)
		{
            if (string.IsNullOrEmpty(key))
                return false;

			lock (monitor) 
            {
				return displayed_cache.ContainsKey(key) || reuse_pool.ContainsKey(key);
			}
		}

		public bool Remove(string key)
		{
            if (string.IsNullOrEmpty(key))
                return false;

			ISelfDisposingBitmapDrawable tmp = null;
			ISelfDisposingBitmapDrawable reuseTmp = null;
			var result = false;
			lock (monitor) 
            {
				if (displayed_cache.TryGetValue(key, out tmp)) 
                {
					result = displayed_cache.Remove(key);
				} 
                else if (reuse_pool.TryGetValue(key, out reuseTmp)) 
                {
					result = reuse_pool.Remove(key);
				}
				if (tmp != null)
				{
					ProcessRemoval(tmp, evicted: true);
				}
				if (reuseTmp != null)
				{
					ProcessRemoval(reuseTmp, evicted: true);
				}

				return result;
			}
		}

		public bool TryGetValue(string key, out ISelfDisposingBitmapDrawable value)
		{
			lock (monitor) 
            {
				var result = displayed_cache.TryGetValue(key, out value);
				if (result) 
                {
                    reuse_pool.Get(key); // If key is found, its place in the LRU is refreshed
					total_cache_hits++;
                    if (_verboseLogging)
					    log.Debug("Cache hit for key: " + key);
				} 
                else 
                {
					ISelfDisposingBitmapDrawable tmp = null;
					result = reuse_pool.TryGetValue(key, out tmp); // If key is found, its place in the LRU is refreshed
					if (result) {
                        if (_verboseLogging)
                            log.Debug("Cache hit from reuse pool for key: " + key);
						total_cache_hits++;
					}
					value = tmp;
				}
				return result;
			}
		}

		public ISelfDisposingBitmapDrawable this[string index] 
        {
			get 
            {
				lock (monitor) 
                {
					ISelfDisposingBitmapDrawable tmp = null;
					TryGetValue(index, out tmp);
					return tmp;
				}
			}
			set 
            {
				Add(index, value);
			}
		}

		public ICollection<string> Keys 
        {
			get 
            {
				lock (monitor) 
                {
					var cacheKeys = displayed_cache.Keys;
                    var allKeys = new HashSet<string>(cacheKeys);
                    var reuseKeys = reuse_pool.Keys;

                    foreach (var item in reuseKeys)
                    {
                        allKeys.Add(item);
                    }

                    return allKeys;
				}
			}
		}

		public ICollection<ISelfDisposingBitmapDrawable> Values 
        {
			get 
            {
				lock (monitor) 
                {
                    var cacheValues = displayed_cache.Values;
                    var allValues = new HashSet<ISelfDisposingBitmapDrawable>(cacheValues);
                    var reuseValues = reuse_pool.Values;

                    foreach (var item in reuseValues)
                    {
                        allValues.Add(item);
                    }

                    return allValues;
				}
			}
		}

		#endregion

		#region ICollection implementation

		public void Add(KeyValuePair<string, ISelfDisposingBitmapDrawable> item)
		{
			Add(item.Key, item.Value);
		}

		public void Clear()
		{
			lock (monitor) 
            {
				foreach (var k in displayed_cache.Keys.ToList()) 
                {
					Remove(k);
				}

				displayed_cache.Clear();
				reuse_pool.Clear();
			}
		}

		public bool Contains(KeyValuePair<string, ISelfDisposingBitmapDrawable> item)
		{
			return ContainsKey(item.Key);
		}

		public void CopyTo(KeyValuePair<string, ISelfDisposingBitmapDrawable>[] array, int arrayIndex)
		{
			throw new NotImplementedException("CopyTo is not supported");
		}

		public bool Remove(KeyValuePair<string, ISelfDisposingBitmapDrawable> item)
		{
			return Remove(item.Key);
		}

		public int Count
        {
			get 
            {
				lock (monitor) 
                {
                    return Keys.Count;
				}
			}
		}

		public bool IsReadOnly 
        {
			get 
            {
                return false;
			}
		}

		#endregion

		#region IEnumerable implementation

		public IEnumerator<KeyValuePair<string, ISelfDisposingBitmapDrawable>> GetEnumerator()
		{
			List<KeyValuePair<string, ISelfDisposingBitmapDrawable>> values;
			lock (monitor) {
				values = new List<KeyValuePair<string, ISelfDisposingBitmapDrawable>>(Count);
				foreach (var k in Keys) {
					values.Add(new KeyValuePair<string, ISelfDisposingBitmapDrawable>(k, this[k]));
				}
			}
			foreach (var kvp in values) {
				yield return kvp;
			}
		}

		#endregion

		#region IEnumerable implementation

		System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
		{
			return GetEnumerator();
		}

		#endregion

		void DebugDumpStats()
		{
			main_thread_handler.PostDelayed(DebugDumpStats, (long)debug_dump_interval.TotalMilliseconds);

			lock (monitor) {
				Log.Debug(TAG, "--------------------");
				Log.Debug(TAG, "current total count: " + Count);
				Log.Debug(TAG, "cumulative additions: " + total_added);
				Log.Debug(TAG, "cumulative removals: " + total_removed);
				Log.Debug(TAG, "total evictions: " + total_evictions);
				Log.Debug(TAG, "total cache hits: " + total_cache_hits);
				Log.Debug(TAG, "reuse hits: " + total_reuse_hits);
				Log.Debug(TAG, "reuse misses: " + total_reuse_misses);
				Log.Debug(TAG, "reuse pool count: " + reuse_pool.Count);
				Log.Debug(TAG, "cache size in bytes:   " + current_cache_byte_count);
				Log.Debug(TAG, "reuse pool in bytes:   " + reuse_pool.CacheSizeInBytes);
				Log.Debug(TAG, "high watermark:        " + high_watermark);
				Log.Debug(TAG, "low watermark:         " + low_watermark);
				if (total_reuse_hits > 0 || total_reuse_misses > 0) {
					Log.Debug(TAG, "reuse hit %: " + (100f * (total_reuse_hits / (float)(total_reuse_hits + total_reuse_misses))));
				}
			}
		}
	}
}
