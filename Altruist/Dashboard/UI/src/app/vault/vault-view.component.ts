// src/app/vault-view/vault-view.component.ts

import { CommonModule } from '@angular/common';
import { Component, OnDestroy, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Subscription } from 'rxjs';
import { VaultDashboardService } from './vault-dashboard.service';
import { VaultDefinitionDto, VaultItemPageDto } from './vault.model';

@Component({
  selector: 'app-vault-view',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './vault-view.component.html',
  styleUrl: './vault-view.component.scss',
})
export class VaultViewComponent implements OnInit, OnDestroy {
  // ---------- vault list ----------
  vaults: VaultDefinitionDto[] = [];
  filteredVaults: VaultDefinitionDto[] = [];
  selectedVault: VaultDefinitionDto | null = null;

  vaultFilter = '';
  isLoadingVaults = false;
  isLoadingItems = false;
  error: string | null = null;

  // ---------- paging ----------
  pageSizeOptions = [10, 25, 50, 100];
  pageSize = 25;
  currentPage = 0;
  totalItems = 0;

  // ---------- data ----------
  items: Record<string, any>[] = [];
  originalItems: Record<string, any>[] = [];

  // ---------- dirty tracking ----------
  dirtyItems = new Map<number, Record<string, any>>();
  hasPendingChanges = false;

  private sub?: Subscription;

  constructor(private readonly service: VaultDashboardService) {}

  // ================= lifecycle =================

  ngOnInit(): void {
    this.loadVaults();
  }

  ngOnDestroy(): void {
    if (this.hasPendingChanges) {
      const ok = confirm(
        'You have uncommitted changes. Leaving will discard them. Continue?'
      );
      if (!ok) return;
    }
    this.sub?.unsubscribe();
  }

  // ================= computed =================

  get hasVaults(): boolean {
    return this.vaults.length > 0;
  }

  get hasItems(): boolean {
    return this.items.length > 0;
  }

  get visibleColumns() {
    return this.selectedVault?.columns ?? [];
  }

  get totalPages(): number {
    return this.pageSize > 0
      ? Math.max(1, Math.ceil(this.totalItems / this.pageSize))
      : 1;
  }

  get displayPage(): number {
    return this.currentPage + 1;
  }

  get statusText(): string {
    if (this.isLoadingVaults) return 'Loading vaults…';
    if (!this.hasVaults) return 'No vaults registered.';
    if (!this.selectedVault) return 'Select a vault to inspect.';
    if (this.isLoadingItems) return 'Loading items…';

    if (this.totalItems === 0) {
      return `Vault '${this.selectedVault.clrTypeShort}' is empty.`;
    }

    const start = this.currentPage * this.pageSize + 1;
    const end = Math.min(
      this.totalItems,
      (this.currentPage + 1) * this.pageSize
    );

    return `${start} – ${end} of ${this.totalItems} items`;
  }

  // ================= loading =================

  loadVaults(): void {
    this.isLoadingVaults = true;
    this.error = null;

    this.service.getVaults().subscribe({
      next: (vaults) => {
        this.vaults = vaults;
        this.applyVaultFilter();
        this.isLoadingVaults = false;

        if (!this.selectedVault && vaults.length > 0) {
          this.onSelectVault(vaults[0]);
        }
      },
      error: () => {
        this.error = 'Failed to load vaults.';
        this.isLoadingVaults = false;
      },
    });
  }

  applyVaultFilter(): void {
    const ft = this.vaultFilter.trim().toLowerCase();
    if (!ft) {
      this.filteredVaults = this.vaults;
      return;
    }

    this.filteredVaults = this.vaults.filter((v) => {
      return (
        v.clrTypeShort.toLowerCase().includes(ft) ||
        v.typeKey.toLowerCase().includes(ft) ||
        v.keyspace.toLowerCase().includes(ft) ||
        v.tableName.toLowerCase().includes(ft)
      );
    });
  }

  onSelectVault(vault: VaultDefinitionDto): void {
    if (this.selectedVault === vault) return;

    if (this.hasPendingChanges) {
      const ok = confirm(
        'You have uncommitted changes. Discard them and switch vault?'
      );
      if (!ok) return;
    }

    this.selectedVault = vault;
    this.currentPage = 0;
    this.dirtyItems.clear();
    this.hasPendingChanges = false;
    this.loadPage();
  }

  // ================= paging =================

  onPageSizeChange(): void {
    this.currentPage = 0;
    this.loadPage();
  }

  goPrevPage(): void {
    if (this.currentPage <= 0 || this.isLoadingItems) return;
    this.currentPage--;
    this.loadPage();
  }

  goNextPage(): void {
    if (this.currentPage >= this.totalPages - 1 || this.isLoadingItems) return;

    this.currentPage++;
    this.loadPage();
  }

  private loadPage(): void {
    if (!this.selectedVault) return;

    this.isLoadingItems = true;
    this.error = null;

    const skip = this.currentPage * this.pageSize;

    this.sub?.unsubscribe();
    this.sub = this.service
      .getVaultItems(this.selectedVault.typeKey, skip, this.pageSize)
      .subscribe({
        next: (page: VaultItemPageDto) => {
          this.items = page.items;
          this.originalItems = JSON.parse(JSON.stringify(page.items));
          this.totalItems = page.total;
          this.isLoadingItems = false;
          this.dirtyItems.clear();
          this.hasPendingChanges = false;
        },
        error: () => {
          this.error = 'Failed to load vault items.';
          this.isLoadingItems = false;
        },
      });
  }

  // ================= editing =================

  onCellEdit(rowIndex: number, field: string, value: any): void {
    const original = this.originalItems[rowIndex]?.[field];

    if (value === original) {
      const entry = this.dirtyItems.get(rowIndex);
      if (entry) {
        delete entry[field];
        if (Object.keys(entry).length === 0) {
          this.dirtyItems.delete(rowIndex);
        }
      }
    } else {
      const entry = this.dirtyItems.get(rowIndex) ?? {};
      entry[field] = value;
      this.dirtyItems.set(rowIndex, entry);
    }

    this.hasPendingChanges = this.dirtyItems.size > 0;
  }

  commitChanges(): void {
    if (!this.selectedVault || this.dirtyItems.size === 0) return;

    const payload = {
      items: Array.from(this.dirtyItems.entries()).map(
        ([rowIndex, changes]) => ({
          ...this.pickPrimaryKeys(this.items[rowIndex]),
          ...changes,
        })
      ),
    };

    this.service.batchUpdate(this.selectedVault.typeKey, payload).subscribe({
      next: () => this.loadPage(),
      error: () => {
        this.error = 'Failed to commit changes.';
      },
    });
  }

  private pickPrimaryKeys(item: Record<string, any>): Record<string, any> {
    const result: Record<string, any> = {};
    for (const col of this.visibleColumns) {
      if (col.isPrimaryKey) {
        result[col.fieldName] = item[col.fieldName];
      }
    }
    return result;
  }
}
