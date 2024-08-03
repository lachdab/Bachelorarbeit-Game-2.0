namespace TPSBR
{
	using UnityEngine;
	using Fusion.Addons.KCC;

	// WARNING!
	// This processor is a special variant of EnvironmentProcessor and has optimizations which would normally be considered as KCC anti-pattern,
	// but these optimizations are required to shrink down performance overhead to minimum.
	// 1. All stages (ISetDynamicVelocity, ISetKinematicDirection, ISetKinematicTangent, ISetKinematicSpeed, ISetKinematicVelocity) were collapsed in one stage (IPrepareData).
	// 2. Step-Up and Ground-Snap processors are executed directly from this processor from IAfterMoveStep stage.
	public sealed class BREnvironmentProcessor : KCCProcessor, IPrepareData, IAfterMoveStep
	{
		// CONSTANTS

		public static readonly int DefaultPriority = EnvironmentProcessor.DefaultPriority;

		// PUBLIC MEMBERS

		[Header("General")]
		[Tooltip("Maximum allowed speed the KCC can move with player input.")]
		public float KinematicSpeed = 8.0f;
		[Tooltip("Custom jump multiplier.")]
		public float JumpMultiplier = 1.0f;
		[Tooltip("Custom gravity. Physics.gravity is used if default.")]
		public Vector3 Gravity = default;
		[Tooltip("Relative environment priority. Default environment processor priority is 1000.")]
		public int   RelativePriority = 0;

		[Header("Ground")]
		[Tooltip("Maximum angle of walkable ground.")]
		public float MaxGroundAngle = 60.0f;
		[Tooltip("Dynamic velocity is decelerated by actual dynamic speed multiplied by this. The faster KCC moves, the more deceleration is applied.")]
		public float DynamicGroundFriction = 20.0f;
		[Tooltip("Kinematic velocity is accelerated by calculated kinematic speed multiplied by this.")]
		public float KinematicGroundAcceleration = 50.0f;
		[Tooltip("Kinematic velocity is decelerated by actual kinematic speed multiplied by this. The faster KCC moves, the more deceleration is applied.")]
		public float KinematicGroundFriction = 35.0f;

		[Header("Air")]
		[Tooltip("Dynamic velocity is decelerated by actual dynamic speed multiplied by this. The faster KCC moves, the more deceleration is applied.")]
		public float DynamicAirFriction = 2.0f;
		[Tooltip("Kinematic velocity is accelerated by calculated kinematic speed multiplied by this.")]
		public float KinematicAirAcceleration = 5.0f;
		[Tooltip("Kinematic velocity is decelerated by actual kinematic speed multiplied by this. The faster KCC moves, the more deceleration is applied.")]
		public float KinematicAirFriction = 2.0f;

		// PRIVATE MEMBERS

		[Header("BR200")]
		[SerializeField]
		private float _speedMultiplier = 1.0f;
		[SerializeField]
		private float _upGravityMultiplier = 1.0f;
		[SerializeField]
		private float _downGravityMultiplier = 1.0f;

		[SerializeField]
		private StepUpProcessor _stepUpProcessor;
		[SerializeField]
		private GroundSnapProcessor _groundSnapProcessor;

		// KCCProcessor INTERFACE

		public override float GetPriority(KCC kcc) => DefaultPriority + RelativePriority;

		// IPrepareData INTERFACE

		public void Execute(PrepareData stage, KCC kcc, KCCData data)
		{
			data.MaxGroundAngle = MaxGroundAngle;
			data.Gravity        = Gravity != default ? Gravity : Physics.gravity;

			data.Gravity *= kcc.FixedData.RealVelocity.y > 0.0f ? _upGravityMultiplier : _downGravityMultiplier;

			SetDynamicVelocity(kcc, data);

			data.KinematicDirection = data.InputDirection.OnlyXZ();

			SetKinematicVelocity(kcc, data);
		}

		private void SetDynamicVelocity(KCC kcc, KCCData data)
		{
			// This code path can be executed in both fixed/render updates.
			// Next fixed/render value is based on values from last fixed update to not introduce error accumulation due to render delta time integration.

			// ERROR ACCUMULATION EXAMPLE
			// ========================================
			// Input:     x(0)     = 1.0f;
			// Operation: x(n + 1) = x(n) * (1.0f + data.DeltaTime);
			// Delta time for fixed  update (60Hz):  0.016667f
			// Delta time for render update (240Hz): 0.004167f

			// RENDER UPDATE PREDICTION (240Hz, 4 frames)
			// ========================================
			// Result (frame 1):    x(r1) = x(0)  + x(0)  * 0.004167f = 1.0f      * (1.0f + 0.004167f) = 1.004167f
			// Result (frame 2):    x(r2) = x(r1) + x(r1) * 0.004167f = 1.004167f * (1.0f + 0.004167f) = 1.008351f
			// Result (frame 3):    x(r3) = x(r2) + x(r2) * 0.004167f = 1.008351f * (1.0f + 0.004167f) = 1.012552f
			// Result (frame 4):    x(r4) = x(r3) + x(r3) * 0.004167f = 1.012552f * (1.0f + 0.004167f) = 1.016771f

			// FIXED UPDATE PREDICTION (60Hz, 1 tick)
			// ========================================
			// Result (tick 1):     x(f1) = x(0)  + x(0)  * 0.016667f = 1.0f * (1.0f + 0.016667f) = 1.016667f

			// THE PROBLEM
			// ========================================
			// Ideally we want mutiple render updates (total delta time equal to single fixed update) to match the fixed update: x(f1) == x(r4).
			// Example above has a render update error: e(r) = x(f1) - x(r4) = 1.016667f - 1.016771f = -0.000104f
			// This is only a demonstration and the error can result to a perceivable jitter, depends on error amplification.

			// THE SOLUTION
			// ========================================
			// The trick is to make all intermediate calculations with FIXED update delta time (0.016667f).
			// And use RENDER update delta time (0.004167f) only to calculate final position delta.
			// This way 4 render position deltas will match 1 fixed position delta.

			// This is the reason why fixedData is sometimes used instead of data (except values calculated in recently executed processors chain, these are safe to use).

			// We explicitly need data from fixed update.
			KCCData fixedData       = kcc.FixedData;
			float   fixedDeltaTime  = fixedData.DeltaTime;
			Vector3 dynamicVelocity = fixedData.DynamicVelocity;

			if (fixedData.IsGrounded == false || (fixedData.IsSteppingUp == false && (fixedData.IsSnappingToGround == true || fixedData.GroundDistance > 0.001f)))
			{
				// Applying gravity only while in the air (not grounded) and not stepping up, ground snapping can be active.
				dynamicVelocity += data.Gravity * fixedDeltaTime;
			}

			if (data.JumpImpulse.IsZero() == false && JumpMultiplier > 0.0f)
			{
				Vector3 jumpDirection = data.JumpImpulse.normalized;

				// Elimination of dynamic velocity in direction of jump, otherwise the jump trajectory would be distorted.
				dynamicVelocity -= Vector3.Scale(dynamicVelocity, jumpDirection);

				// Applying jump impulse.
				dynamicVelocity += data.JumpImpulse * JumpMultiplier;

				// Increase jump counter.
				// For fixed updates this counter will be 1 at max.
				// For render updates this counter can reach higher values, indicating how many times the JumpImpulse was applied on top of fixed data.
				++data.JumpFrames;
			}

			// Apply external forces.
			dynamicVelocity += data.ExternalVelocity;
			dynamicVelocity += data.ExternalAcceleration * fixedDeltaTime;
			dynamicVelocity += data.ExternalImpulse;
			dynamicVelocity += data.ExternalForce * fixedDeltaTime;

			if (dynamicVelocity.IsZero() == false)
			{
				if (dynamicVelocity.IsAlmostZero(0.001f) == true)
				{
					// Clamping values near zero.
					dynamicVelocity = default;
				}
				else
				{
					// Applying ground (XYZ) and air (XZ) friction.
					if (fixedData.IsGrounded == true)
					{
						Vector3 frictionAxis = Vector3.one;
						if (fixedData.GroundDistance > 0.001f || fixedData.IsSnappingToGround == true)
						{
							frictionAxis.y = default;
						}

						dynamicVelocity += KCCPhysicsUtility.GetFriction(dynamicVelocity, dynamicVelocity, frictionAxis, fixedData.GroundNormal, fixedData.KinematicSpeed, true, 0.0f, 0.0f, DynamicGroundFriction, fixedDeltaTime);
					}
					else
					{
						dynamicVelocity += KCCPhysicsUtility.GetFriction(dynamicVelocity, dynamicVelocity, new Vector3(1.0f, 0.0f, 1.0f), fixedData.KinematicSpeed, true, 0.0f, 0.0f, DynamicAirFriction, fixedDeltaTime);
					}
				}
			}

			data.DynamicVelocity = dynamicVelocity;

			if (kcc.IsInFixedUpdate == true)
			{
				// Consume one-time effects only in fixed update.
				// For render prediction we need them to be applied on top of fixed data in all frames.
				data.JumpImpulse      = default;
				data.ExternalVelocity = default;
				data.ExternalImpulse  = default;
			}

			// Forces applied over-time are reset always. These are set every tick/frame.
			data.ExternalAcceleration = default;
			data.ExternalForce        = default;
		}

		private void SetKinematicVelocity(KCC kcc, KCCData data)
		{
			// SetKinematicTangent

			KCCData fixedData         = kcc.FixedData;
			float   fixedDeltaTime    = fixedData.DeltaTime;
			Vector3 kinematicVelocity = fixedData.KinematicVelocity;

			if (fixedData.IsGrounded == true)
			{
				// The character is grounded.

				if (data.KinematicDirection.IsAlmostZero(0.0001f) == false && KCCPhysicsUtility.ProjectOnGround(fixedData.GroundNormal, data.KinematicDirection, out Vector3 projectedMoveDirection) == true)
				{
					// Use projected kinematic direction on ground when possible.
					data.KinematicTangent = projectedMoveDirection.normalized;
				}
				else
				{
					// Otherwise use ground tangent => steepest descent.
					data.KinematicTangent = fixedData.GroundTangent;
				}
			}
			else
			{
				// The character is floating in the air.

				if (data.KinematicDirection.IsAlmostZero(0.0001f) == false)
				{
					// Use kinematic direction directly.
					data.KinematicTangent = data.KinematicDirection.normalized;
				}
				else
				{
					// No direction set, use character forward.
					data.KinematicTangent = data.TransformDirection;
				}
			}

			// SetKinematicSpeed

			data.KinematicSpeed = kcc.MoveState.GetBaseSpeed((Quaternion.Inverse(data.TransformRotation) * data.KinematicDirection).XZ0().normalized, default);
			data.KinematicSpeed *= _speedMultiplier;

			// SetKinematicVelocity

			// This code path can be executed in both fixed/render updates.
			// Next fixed/render value is based on values from last fixed update to not introduce error accumulation due to render delta time integration.
			// Details are described above in SetDynamicVelocity().

			if (fixedData.IsGrounded == true)
			{
				// The character is grounded.

				if (kinematicVelocity.IsAlmostZero() == false && KCCPhysicsUtility.ProjectOnGround(fixedData.GroundNormal, kinematicVelocity, out Vector3 projectedKinematicVelocity) == true)
				{
					// Project current velocity on ground.
					kinematicVelocity = projectedKinematicVelocity.normalized * kinematicVelocity.magnitude;
				}

				if (data.KinematicDirection.IsAlmostZero() == true)
				{
					// No kinematic direction
					// Just apply friction (XYZ) and exit early.
					data.KinematicVelocity = kinematicVelocity + KCCPhysicsUtility.GetFriction(kinematicVelocity, kinematicVelocity, Vector3.one, fixedData.GroundNormal, data.KinematicSpeed, true, 0.0f, 0.0f, KinematicGroundFriction, fixedDeltaTime);
					return;
				}
			}
			else
			{
				// The character is floating in the air.

				if (data.KinematicDirection.IsAlmostZero() == true)
				{
					// No kinematic direction
					// Just apply friction (XZ) and exit early.
					data.KinematicVelocity = kinematicVelocity + KCCPhysicsUtility.GetFriction(kinematicVelocity, kinematicVelocity, new Vector3(1.0f, 0.0f, 1.0f), data.KinematicSpeed, true, 0.0f, 0.0f, KinematicAirFriction, fixedDeltaTime);
					return;
				}
			}

			// Following section calculates ground/air acceleration and friction relatively to move direction and kinematic tangent.
			// And combines them together with base kinematic velocity.

			Vector3 moveDirection = kinematicVelocity;
			if (moveDirection.IsZero() == true)
			{
				moveDirection = data.KinematicTangent;
			}

			Vector3 acceleration;
			Vector3 friction;

			if (fixedData.IsGrounded == true)
			{
				acceleration = KCCPhysicsUtility.GetAcceleration(kinematicVelocity, data.KinematicTangent, Vector3.one, data.KinematicSpeed, false, data.KinematicDirection.magnitude, 0.0f, KinematicGroundAcceleration, 0.0f, fixedDeltaTime);
				friction     = KCCPhysicsUtility.GetFriction(kinematicVelocity, moveDirection, Vector3.one, fixedData.GroundNormal, data.KinematicSpeed, false, 0.0f, 0.0f, KinematicGroundFriction, fixedDeltaTime);
			}
			else
			{
				acceleration = KCCPhysicsUtility.GetAcceleration(kinematicVelocity, data.KinematicTangent, Vector3.one, data.KinematicSpeed, false, data.KinematicDirection.magnitude, 0.0f, KinematicAirAcceleration, 0.0f, fixedDeltaTime);
				friction     = KCCPhysicsUtility.GetFriction(kinematicVelocity, moveDirection, new Vector3(1.0f, 0.0f, 1.0f), data.KinematicSpeed, false, 0.0f, 0.0f, KinematicAirFriction, fixedDeltaTime);
			}

			kinematicVelocity = KCCPhysicsUtility.CombineAccelerationAndFriction(kinematicVelocity, acceleration, friction);

			// Clamp velocity.
			if (kinematicVelocity.sqrMagnitude > data.KinematicSpeed * data.KinematicSpeed)
			{
				kinematicVelocity = kinematicVelocity / Vector3.Magnitude(kinematicVelocity) * data.KinematicSpeed;
			}

			// Reset Y axis to get stable jump height results even if moving downwards.
			if (data.JumpFrames > 0 && kinematicVelocity.y < 0.0f)
			{
				kinematicVelocity.y = 0.0f;
			}

			data.KinematicVelocity = kinematicVelocity;
		}

		// IAfterMoveStep INTERFACE

		public void Execute(AfterMoveStep stage, KCC kcc, KCCData data)
		{
			// This code path can be executed multiple times in single update if CCD is active (Continuous Collision Detection).

			KCCData fixedData = kcc.FixedData;

			if (data.IsGrounded == true)
			{
				if (fixedData.WasGrounded == true && data.IsSnappingToGround == false && data.DynamicVelocity.y < 0.0f && data.DynamicVelocity.OnlyXZ().IsAlmostZero() == true)
				{
					// Reset dynamic velocity Y axis while grounded (to not accumulate gravity indefinitely and clamp to precise zero).
					data.DynamicVelocity.y = 0.0f;
				}

				if (fixedData.WasGrounded == false)
				{
					if (data.KinematicVelocity.OnlyXZ().IsAlmostZero() == true)
					{
						// Reset Y axis after getting grounded and there is no horizontal movement.
						data.KinematicVelocity.y = 0.0f;
					}
					else
					{
						// Otherwise try projecting kinematic velocity onto ground.
						if (KCCPhysicsUtility.ProjectOnGround(data.GroundNormal, data.KinematicVelocity, out Vector3 projectedKinematicVelocity) == true)
						{
							data.KinematicVelocity = projectedKinematicVelocity.normalized * data.KinematicVelocity.magnitude;
						}
					}
				}
			}
			else
			{
				if (fixedData.WasGrounded == false && data.DynamicVelocity.y > 0.0f && data.DeltaTime > 0.0f)
				{
					Vector3 currentVelocity;

					if (kcc.IsInFixedUpdate == true)
					{
						currentVelocity = (data.TargetPosition - data.BasePosition) / data.DeltaTime;
					}
					else
					{
						currentVelocity = (data.TargetPosition - fixedData.TargetPosition) / (fixedData.DeltaTime * data.Alpha);
					}

					if (currentVelocity.y.IsAlmostZero() == true)
					{
						// Clamping dynamic up velocity if there is no real position change => hitting a roof.
						data.DynamicVelocity.y = 0.0f;
					}
				}
			}

			_stepUpProcessor.Execute(stage, kcc, data);
			_groundSnapProcessor.Execute(stage, kcc, data);
		}
	}
}
