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

  private subscription: Subscription | null = null;

  constructor(private readonly cacheService: CacheDashboardService) {}

  ngOnInit(): void {
    this.loadEntries();
  }

  ngOnDestroy(): void {
    this.subscription?.unsubscribe();
  }

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

  get statusHint(): string {
    if (this.isLoading) return 'Loading cache…';
    if (!this.hasData) return 'No cache entries.';
    return `Cache entries · Total: ${this.entries.length}`;
  }

  loadEntries(): void {
    this.entries = [];
    this.isLoading = true;
    this.hasData = false;
    this.error = null;

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
        },
        error: (err) => {
          console.error(err);
          this.error = 'Failed to delete cache entry.';
        },
      });
  }
}
