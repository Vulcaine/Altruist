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
  vaults: VaultDefinitionDto[] = [];
  filteredVaults: VaultDefinitionDto[] = [];
  selectedVault: VaultDefinitionDto | null = null;

  vaultFilter = '';
  isLoadingVaults = false;
  isLoadingItems = false;
  error: string | null = null;

  // paging
  pageSizeOptions = [10, 25, 50, 100];
  pageSize = 25;
  currentPage = 0;
  totalItems = 0;

  // current page items
  fields: string[] = [];
  items: Record<string, any>[] = [];

  private sub?: Subscription;

  constructor(private readonly service: VaultDashboardService) {}

  ngOnInit(): void {
    this.loadVaults();
  }

  ngOnDestroy(): void {
    this.sub?.unsubscribe();
  }

  get hasVaults(): boolean {
    return this.vaults.length > 0;
  }

  get hasItems(): boolean {
    return this.items.length > 0;
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

  get visibleColumns() {
    return this.selectedVault?.columns ?? [];
  }

  loadVaults(): void {
    this.isLoadingVaults = true;
    this.error = null;

    this.service.getVaults().subscribe({
      next: (vaults) => {
        this.vaults = vaults;
        this.applyVaultFilter();
        this.isLoadingVaults = false;

        if (!this.selectedVault && this.vaults.length > 0) {
          this.onSelectVault(this.vaults[0]);
        }
      },
      error: (err) => {
        console.error(err);
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
    this.selectedVault = vault;
    this.currentPage = 0;
    this.loadPage();
  }

  onPageSizeChange(): void {
    this.currentPage = 0;
    this.loadPage();
  }

  goPrevPage(): void {
    if (this.currentPage <= 0) return;
    this.currentPage--;
    this.loadPage();
  }

  goNextPage(): void {
    if (this.currentPage >= this.totalPages - 1) return;
    this.currentPage++;
    this.loadPage();
  }

  private loadPage(): void {
    if (!this.selectedVault) return;

    this.isLoadingItems = true;
    this.error = null;

    const skip = this.currentPage * this.pageSize;
    const take = this.pageSize;

    this.sub?.unsubscribe();
    this.sub = this.service
      .getVaultItems(this.selectedVault.typeKey, skip, take)
      .subscribe({
        next: (page: VaultItemPageDto) => {
          this.fields = page.fields;
          this.items = page.items;
          this.totalItems = page.total;
          this.isLoadingItems = false;
        },
        error: (err) => {
          console.error(err);
          this.error = 'Failed to load vault items.';
          this.isLoadingItems = false;
        },
      });
  }

  prettyValue(value: any): string {
    if (value === null || value === undefined) return '—';
    if (typeof value === 'string') return value;
    if (typeof value === 'number' || typeof value === 'boolean') {
      return String(value);
    }
    // Fallback: JSON snippet
    try {
      const json = JSON.stringify(value);
      return json.length > 80 ? json.slice(0, 77) + '…' : json;
    } catch {
      return String(value);
    }
  }
}
