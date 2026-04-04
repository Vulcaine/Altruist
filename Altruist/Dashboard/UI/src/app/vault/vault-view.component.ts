import { CommonModule } from '@angular/common';
import { Component, OnDestroy, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Subscription } from 'rxjs';
import { VaultDashboardService } from './vault-dashboard.service';
import {
  VaultColumnDto,
  VaultDefinitionDto,
  VaultItemPageDto,
} from './vault.model';

@Component({
  selector: 'app-vault-view',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './vault-view.component.html',
  styleUrl: './vault-view.component.scss',
})
export class VaultViewComponent implements OnInit, OnDestroy {
  vaults: VaultDefinitionDto[] = [];
  filteredVaults: VaultDefinitionDto[] = [];
  groupedVaults: { keyspace: string; vaults: VaultDefinitionDto[] }[] = [];

  collapsedKeyspaces = new Set<string>();

  selectedVault: VaultDefinitionDto | null = null;

  vaultFilter = '';
  isLoadingVaults = false;
  isLoadingItems = false;
  error: string | null = null;

  pageSizeOptions = [10, 25, 50, 100];
  pageSize = 25;
  currentPage = 0;
  totalItems = 0;

  items: Record<string, any>[] = [];
  originalItems: Record<string, any>[] = [];

  editing = new Set<string>();
  dirtyItems = new Map<number, Record<string, any>>();
  hasPendingChanges = false;

  private sub?: Subscription;

  constructor(private readonly service: VaultDashboardService) {}

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

  // --------------------------
  // GROUPING
  // --------------------------

  private recomputeGroups(): void {
    const map = new Map<string, VaultDefinitionDto[]>();

    for (const v of this.filteredVaults) {
      if (!map.has(v.keyspace)) map.set(v.keyspace, []);
      map.get(v.keyspace)!.push(v);
    }

    this.groupedVaults = Array.from(map.entries()).map(
      ([keyspace, vaults]) => ({
        keyspace,
        vaults,
      })
    );
  }

  toggleKeyspace(keyspace: string): void {
    if (this.collapsedKeyspaces.has(keyspace)) {
      this.collapsedKeyspaces.delete(keyspace);
    } else {
      this.collapsedKeyspaces.add(keyspace);
    }
  }

  // --------------------------
  // ORDER PK FIRST
  // --------------------------

  get orderedColumns(): VaultColumnDto[] {
    if (!this.selectedVault?.columns) return [];
    const pk = this.selectedVault.columns.filter((c) => c.isPrimaryKey);
    const rest = this.selectedVault.columns.filter((c) => !c.isPrimaryKey);
    return [...pk, ...rest];
  }

  // --------------------------
  // STATUS / COMPUTED
  // --------------------------

  get hasVaults() {
    return this.vaults.length > 0;
  }

  get hasItems() {
    return this.items.length > 0;
  }

  get totalPages() {
    return Math.max(1, Math.ceil(this.totalItems / this.pageSize));
  }

  get displayPage() {
    return this.currentPage + 1;
  }

  get statusText(): string {
    if (this.isLoadingVaults) return 'Loading vaults…';
    if (!this.hasVaults) return 'No vaults registered.';
    if (!this.selectedVault) return 'Select a vault to inspect.';
    if (this.isLoadingItems) return 'Loading items…';

    if (this.totalItems === 0)
      return `Vault '${this.selectedVault.clrTypeShort}' is empty.`;

    const start = this.currentPage * this.pageSize + 1;
    const end = Math.min(
      this.totalItems,
      (this.currentPage + 1) * this.pageSize
    );
    return `${start}–${end} of ${this.totalItems}`;
  }

  // --------------------------
  // LOADING VAULTS
  // --------------------------

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
    this.filteredVaults = !ft
      ? this.vaults
      : this.vaults.filter(
          (v) =>
            v.clrTypeShort.toLowerCase().includes(ft) ||
            v.typeKey.toLowerCase().includes(ft) ||
            v.keyspace.toLowerCase().includes(ft) ||
            v.tableName.toLowerCase().includes(ft)
        );

    this.recomputeGroups();
  }

  onSelectVault(v: VaultDefinitionDto): void {
    if (this.hasPendingChanges) {
      const ok = confirm(
        'You have uncommitted changes. Discard them and switch vault?'
      );
      if (!ok) return;
    }

    this.selectedVault = v;
    this.currentPage = 0;
    this.editing.clear();
    this.dirtyItems.clear();
    this.hasPendingChanges = false;
    this.loadPage();
  }

  // --------------------------
  // PAGINATION
  // --------------------------

  onPageSizeChange() {
    this.currentPage = 0;
    this.loadPage();
  }

  goPrevPage() {
    if (this.currentPage > 0) {
      this.currentPage--;
      this.loadPage();
    }
  }

  goNextPage() {
    if (this.currentPage < this.totalPages - 1) {
      this.currentPage++;
      this.loadPage();
    }
  }

  private loadPage() {
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
          this.editing.clear();
          this.dirtyItems.clear();
          this.hasPendingChanges = false;
        },
        error: () => {
          this.error = 'Failed to load vault items.';
          this.isLoadingItems = false;
        },
      });
  }

  // --------------------------
  // EDITING
  // --------------------------

  startEdit(row: number, field: string) {
    this.editing.add(`${row}:${field}`);
  }

  isEditing(row: number, field: string) {
    return this.editing.has(`${row}:${field}`);
  }

  onCellEdit(row: number, field: string, value: any) {
    const original = this.originalItems[row]?.[field];

    if (value === original) {
      const entry = this.dirtyItems.get(row);
      if (entry) {
        delete entry[field];
        if (Object.keys(entry).length === 0) this.dirtyItems.delete(row);
      }
    } else {
      const entry = this.dirtyItems.get(row) ?? {};
      entry[field] = value;
      this.dirtyItems.set(row, entry);
    }

    this.hasPendingChanges = this.dirtyItems.size > 0;
  }

  onCellBlur(row: number, field: string) {
    const key = `${row}:${field}`;
    const original = this.originalItems[row]?.[field];
    const current = this.items[row]?.[field];

    if (current === original) this.editing.delete(key);
  }

  // --------------------------
  // COMMIT
  // --------------------------

  commitChanges() {
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
      error: () => (this.error = 'Failed to commit changes.'),
    });
  }

  private pickPrimaryKeys(item: Record<string, any>): Record<string, any> {
    const out: Record<string, any> = {};
    for (const col of this.orderedColumns) {
      if (col.isPrimaryKey) out[col.fieldName] = item[col.fieldName];
    }
    return out;
  }
}
