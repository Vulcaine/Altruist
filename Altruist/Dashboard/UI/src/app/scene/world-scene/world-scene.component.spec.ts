import { ComponentFixture, TestBed } from '@angular/core/testing';

import { WorldSceneComponent } from './world-scene.component';

describe('WorldSceneComponent', () => {
  let component: WorldSceneComponent;
  let fixture: ComponentFixture<WorldSceneComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [WorldSceneComponent]
    })
    .compileComponents();

    fixture = TestBed.createComponent(WorldSceneComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('should create', () => {
    expect(component).toBeTruthy();
  });
});
