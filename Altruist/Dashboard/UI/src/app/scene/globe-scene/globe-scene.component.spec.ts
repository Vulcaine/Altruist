import { ComponentFixture, TestBed } from '@angular/core/testing';

import { GlobeSceneComponent } from './globe-scene.component';

describe('GlobeScene', () => {
  let component: GlobeSceneComponent;
  let fixture: ComponentFixture<GlobeSceneComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [GlobeSceneComponent],
    }).compileComponents();

    fixture = TestBed.createComponent(GlobeSceneComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('should create', () => {
    expect(component).toBeTruthy();
  });
});
