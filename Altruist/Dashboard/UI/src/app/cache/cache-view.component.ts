import { CommonModule } from '@angular/common';
import { Component, OnDestroy, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Subscription } from 'rxjs';
import { CacheEntryDto } from './cache.model';
import { CacheDashboardService } from './cache.service';

@Component({
  selector: 'app-cache-view',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './cache-view.component.html',
  styleUrl: './cache-view.component.scss',
})
export class CacheViewComponent implements OnInit, OnDestroy {
  entries: CacheEntryDto[] = [];
  isLoading = false;
  hasData = false;
  error: string | null = null;

  filterText = '';
  selectedEntry: CacheEntryDto | null = null;

  editorVisible = false;
  editorJson = '';
  editorError: string | null = null;

  // pagination config
  readonly pageSizeOptions = [10, 25, 50, 100];
  pageSize = this.pageSizeOptions[0];
  currentPage = 0;

  private subscription: Subscription | null = null;

  constructor(private readonly cacheService: CacheDashboardService) {}

  ngOnInit(): void {
    this.loadEntries();
  }

  ngOnDestroy(): void {
    this.subscription?.unsubscribe();
  }

  /** All entries filtered by filterText (no paging). */
  get filteredEntries(): CacheEntryDto[] {
    const ft = this.filterText.toLowerCase();
    if (!ft) return this.entries;

    return this.entries.filter((e) => {
      return (
        e.typeShortName.toLowerCase().includes(ft) ||
        (e.groupId ?? '').toLowerCase().includes(ft) ||
        e.key.toLowerCase().includes(ft) ||
        (e.preview ?? '').toLowerCase().includes(ft)
      );
    });
  }

  /** Filtered entries of the current page. */
  get pagedEntries(): CacheEntryDto[] {
    const all = this.filteredEntries;
    const start = this.currentPage * this.pageSize;
    const end = start + this.pageSize;

    if (start >= all.length) {
      // if we ended up beyond range (e.g. after delete), fall back to first page
      return all.slice(0, this.pageSize);
    }

    return all.slice(start, end);
  }

  get totalFiltered(): number {
    return this.filteredEntries.length;
  }

  get totalPages(): number {
    if (this.totalFiltered === 0) return 1;
    return Math.max(1, Math.ceil(this.totalFiltered / this.pageSize));
  }

  get showPager(): boolean {
    // only show pager+page-size once we have more entries than the smallest page size
    return this.totalFiltered > this.pageSizeOptions[0];
  }

  get statusHint(): string {
    if (this.isLoading) return 'Loading cache…';
    if (!this.hasData) return 'No cache entries.';

    const total = this.entries.length;
    const filtered = this.totalFiltered;

    if (filtered === 0) {
      return `Cache entries · Filtered 0 results (from ${total} total)`;
    }

    const start = this.currentPage * this.pageSize + 1;
    const end = Math.min(filtered, (this.currentPage + 1) * this.pageSize);

    if (filtered === total) {
      return `Cache entries · Showing ${start}–${end} of ${total}`;
    }

    return `Cache entries · Showing ${start}–${end} of ${filtered} (filtered from ${total})`;
  }

  loadEntries(): void {
    this.entries = [];
    this.isLoading = true;
    this.hasData = false;
    this.error = null;
    this.currentPage = 0;

    this.subscription?.unsubscribe();
    this.subscription = this.cacheService.streamEntries().subscribe({
      next: (entry) => {
        this.entries.push(entry);
        this.hasData = true;
        this.isLoading = false;
      },
      error: (err) => {
        this.error = 'Failed to load cache entries.';
        console.error(err);
        this.isLoading = false;
      },
      complete: () => {
        this.isLoading = false;
      },
    });
  }

  onFilterChange(value: string): void {
    this.filterText = value;
    this.currentPage = 0;
  }

  onPageSizeChange(): void {
    this.currentPage = 0;
  }

  goPrevPage(): void {
    if (this.currentPage <= 0) return;
    this.currentPage--;
  }

  goNextPage(): void {
    if (this.currentPage >= this.totalPages - 1) return;
    this.currentPage++;
  }

  onRowDoubleClick(entry: CacheEntryDto): void {
    this.selectedEntry = entry;
    this.editorError = null;
    this.editorJson = JSON.stringify(entry.value, null, 2);
    this.editorVisible = true;
  }

  closeEditor(): void {
    this.editorVisible = false;
    this.selectedEntry = null;
    this.editorError = null;
  }

  saveEditor(): void {
    if (!this.selectedEntry) return;

    let parsed: any;
    try {
      parsed = JSON.parse(this.editorJson);
    } catch (err) {
      this.editorError = 'Invalid JSON. Please fix and try again.';
      return;
    }

    const updated: CacheEntryDto = {
      ...this.selectedEntry,
      value: parsed,
    };

    this.cacheService.updateEntry(updated).subscribe({
      next: () => {
        // Update local copy
        Object.assign(this.selectedEntry!, updated);
        this.closeEditor();
      },
      error: (err) => {
        console.error(err);
        this.editorError = 'Failed to save changes.';
      },
    });
  }

  deleteEntry(entry: CacheEntryDto, event?: MouseEvent): void {
    event?.stopPropagation();
    if (
      !confirm(`Delete cache entry "${entry.key}" (${entry.typeShortName})?`)
    ) {
      return;
    }

    this.cacheService
      .deleteEntry({ type: entry.type, groupId: entry.groupId, key: entry.key })
      .subscribe({
        next: () => {
          this.entries = this.entries.filter((e) => e !== entry);

          // adjust page if we just emptied the last page
          if (this.currentPage >= this.totalPages) {
            this.currentPage = Math.max(0, this.totalPages - 1);
          }
        },
        error: (err) => {
          console.error(err);
          this.error = 'Failed to delete cache entry.';
        },
      });
  }
}
