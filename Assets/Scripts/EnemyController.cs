using UnityEngine;

public enum EnemyState
{
    Patrol,
    Chase,
    Attack,
    LostPlayer
}

[RequireComponent(typeof(Rigidbody2D))]
public class EnemyController : MonoBehaviour
{
    [SerializeField] private Transform[] waypoints;
    [SerializeField] private float moveSpeed = 2f;
    [SerializeField] private float rotationLerpSpeed = 8f;
    [SerializeField] private float waitDuration = 1f;
    [SerializeField] private float arrivalThreshold = 0.05f;

    [Header("Detection")]
    [SerializeField] private float detectionRadius = 5f;
    [SerializeField] private float fieldOfViewAngle = 90f;
    [SerializeField] private LayerMask obstacleMask;
    [SerializeField] private Vector2 eyeOffset = Vector2.zero;

    [Header("Attack")]
    [SerializeField] private float attackRange = 1.5f;
    [SerializeField] private float attackInterval = 1f;

    [Header("Look Around")]
    [SerializeField] private float lookAroundAngle = 60f;
    [SerializeField] private float sideHoldDuration = 1f;
    [SerializeField] private float forwardHoldDuration = 1.25f;

    [Header("Physics")]
    [SerializeField] private bool ignorePlayerCollision = true;
    [SerializeField] private float stuckTimeout = 2f;

    [SerializeField] private EnemyState state = EnemyState.Patrol;
    private int currentWaypointIndex;
    private float waitTimer;
    private float attackTimer;
    private float loseTargetTimer;
    private float lookAroundBaseAngle;
    private float stuckTimer;
    private float bestWaypointDistance = float.MaxValue;
    private Transform playerTransform;
    private Rigidbody2D body;
    private Collider2D bodyCollider;

    private void Awake()
    {
        body = GetComponent<Rigidbody2D>();
        bodyCollider = GetComponent<Collider2D>();
        body.gravityScale = 0f;
        // We drive rotation manually for the FOV facing; stop physics from spinning us.
        body.freezeRotation = true;
    }

    private void Start()
    {
        GameObject player = GameObject.FindGameObjectWithTag("Player");
        if (player != null)
        {
            playerTransform = player.transform;

            if (ignorePlayerCollision && bodyCollider != null)
            {
                // Let the player pass through the enemy so it can't be shoved off its path.
                foreach (Collider2D playerCollider in player.GetComponentsInChildren<Collider2D>())
                {
                    Physics2D.IgnoreCollision(bodyCollider, playerCollider);
                }
            }
        }

        if (waypoints == null || waypoints.Length == 0)
        {
            Debug.LogWarning($"{nameof(EnemyController)} on {name} has no waypoints assigned.", this);
        }

        if (obstacleMask == 0)
        {
            Debug.LogWarning(
                $"{nameof(EnemyController)} on {name} has no Obstacle Mask set, so it will see " +
                "through walls. Assign your wall layer to enable line-of-sight blocking.", this);
        }
    }

    private void FixedUpdate()
    {
        UpdateState();

        switch (state)
        {
            case EnemyState.Patrol:
                UpdatePatrol();
                break;
            case EnemyState.Chase:
                UpdateChase();
                break;
            case EnemyState.Attack:
                UpdateAttack();
                break;
            case EnemyState.LostPlayer:
                UpdateLookAround();
                break;
        }
    }

    private void UpdateState()
    {
        switch (state)
        {
            case EnemyState.Patrol:
                if (IsPlayerDetected())
                {
                    state = EnemyState.Chase;
                }
                break;

            case EnemyState.Chase:
            case EnemyState.Attack:
                if (playerTransform == null || !IsPlayerWithinDetectionRadius() || !HasLineOfSight())
                {
                    // Lost sight (out of range or behind a wall): look around first.
                    state = EnemyState.LostPlayer;
                    loseTargetTimer = ScanDuration;
                    lookAroundBaseAngle = transform.eulerAngles.z;
                    break;
                }

                state = IsPlayerWithinAttackRange() ? EnemyState.Attack : EnemyState.Chase;
                break;

            case EnemyState.LostPlayer:
                // Re-acquire only if the player is back in range AND visible.
                if (IsPlayerWithinDetectionRadius() && HasLineOfSight())
                {
                    state = IsPlayerWithinAttackRange() ? EnemyState.Attack : EnemyState.Chase;
                    break;
                }

                loseTargetTimer -= Time.deltaTime;
                if (loseTargetTimer <= 0f)
                {
                    state = EnemyState.Patrol;
                }
                break;
        }
    }

    private bool IsPlayerDetected()
    {
        if (!IsPlayerWithinDetectionRadius())
        {
            return false;
        }

        Vector2 toPlayer = playerTransform.position - transform.position;
        Vector2 forward = transform.right;
        float angle = Vector2.Angle(forward, toPlayer);
        if (angle > fieldOfViewAngle * 0.5f)
        {
            return false;
        }

        return HasLineOfSight();
    }

    private bool HasLineOfSight()
    {
        if (playerTransform == null)
        {
            return false;
        }

        Vector2 origin = (Vector2)transform.position + eyeOffset;
        Vector2 target = playerTransform.position;
        Vector2 toTarget = target - origin;
        float distance = toTarget.magnitude;
        if (distance < Mathf.Epsilon)
        {
            return true;
        }

        // A wall between the enemy and the player blocks sight.
        RaycastHit2D hit = Physics2D.Raycast(origin, toTarget / distance, distance, obstacleMask);
        return hit.collider == null;
    }

    private bool IsPlayerWithinDetectionRadius()
    {
        if (playerTransform == null)
        {
            return false;
        }

        Vector2 toPlayer = playerTransform.position - transform.position;
        return toPlayer.sqrMagnitude <= detectionRadius * detectionRadius;
    }

    private bool IsPlayerWithinAttackRange()
    {
        if (playerTransform == null)
        {
            return false;
        }

        Vector2 toPlayer = playerTransform.position - transform.position;
        return toPlayer.sqrMagnitude <= attackRange * attackRange;
    }

    private void UpdatePatrol()
    {
        if (waypoints == null || waypoints.Length == 0)
        {
            return;
        }

        if (waitTimer > 0f)
        {
            waitTimer -= Time.deltaTime;
            return;
        }

        Transform target = waypoints[currentWaypointIndex];
        if (target == null)
        {
            AdvanceWaypoint();
            return;
        }

        Vector2 destination = target.position;
        float distance = Vector2.Distance(body.position, destination);

        if (distance <= arrivalThreshold)
        {
            waitTimer = waitDuration;
            AdvanceWaypoint();
            return;
        }

        // If we can't get closer for a while (e.g. shoved against a wall), give up
        // on this waypoint and move on to the next one.
        if (distance < bestWaypointDistance - 0.01f)
        {
            bestWaypointDistance = distance;
            stuckTimer = 0f;
        }
        else if (stuckTimeout > 0f)
        {
            stuckTimer += Time.deltaTime;
            if (stuckTimer >= stuckTimeout)
            {
                AdvanceWaypoint();
                return;
            }
        }

        Face(destination - body.position);
        MoveToward(destination);
    }

    private void UpdateChase()
    {
        if (playerTransform == null)
        {
            return;
        }

        Vector2 destination = playerTransform.position;

        Face(destination - body.position);
        MoveToward(destination);
    }

    private void MoveToward(Vector2 destination)
    {
        // Physics-based move so walls (colliders) actually block the enemy.
        Vector2 next = Vector2.MoveTowards(body.position, destination, moveSpeed * Time.deltaTime);
        body.MovePosition(next);
    }

    private void UpdateAttack()
    {
        // Keep facing the player while stopped, then strike on an interval.
        if (playerTransform != null)
        {
            Face(playerTransform.position - transform.position);
        }

        attackTimer -= Time.deltaTime;
        if (attackTimer <= 0f)
        {
            Debug.Log($"{name} attacks the player!", this);
            attackTimer = attackInterval;
        }
    }

    private float ScanDuration => sideHoldDuration * 2f + forwardHoldDuration;

    private void UpdateLookAround()
    {
        // Stay put and dwell on each side, then settle facing forward.
        float elapsed = ScanDuration - loseTargetTimer;

        float offsetAngle;
        if (elapsed < sideHoldDuration)
        {
            offsetAngle = lookAroundAngle;        // hold looking to one side
        }
        else if (elapsed < sideHoldDuration * 2f)
        {
            offsetAngle = -lookAroundAngle;       // hold looking to the other side
        }
        else
        {
            offsetAngle = 0f;                     // settle looking forward longer
        }

        float scanAngle = (lookAroundBaseAngle + offsetAngle) * Mathf.Deg2Rad;
        Vector3 scanDirection = new Vector3(Mathf.Cos(scanAngle), Mathf.Sin(scanAngle), 0f);
        Face(scanDirection);
    }

    private void Face(Vector3 direction)
    {
        direction.z = 0f;
        if (direction.sqrMagnitude < 0.0001f)
        {
            return;
        }

        float targetAngle = Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg;
        Quaternion targetRotation = Quaternion.AngleAxis(targetAngle, Vector3.forward);

        // Framerate-independent lerp: eases toward the target each frame.
        float t = 1f - Mathf.Exp(-rotationLerpSpeed * Time.deltaTime);
        transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, t);
    }

    private void AdvanceWaypoint()
    {
        currentWaypointIndex = (currentWaypointIndex + 1) % waypoints.Length;
        stuckTimer = 0f;
        bestWaypointDistance = float.MaxValue;
    }

    private void OnDrawGizmosSelected()
    {
        Vector3 origin = transform.position;

        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(origin, detectionRadius);

        Gizmos.color = Color.red;
        Gizmos.DrawWireSphere(origin, attackRange);

        float halfAngle = fieldOfViewAngle * 0.5f;
        Vector3 forward = transform.right;
        Vector3 leftBound = Quaternion.Euler(0f, 0f, halfAngle) * forward;
        Vector3 rightBound = Quaternion.Euler(0f, 0f, -halfAngle) * forward;

        Gizmos.color = Color.cyan;
        Gizmos.DrawLine(origin, origin + leftBound * detectionRadius);
        Gizmos.DrawLine(origin, origin + rightBound * detectionRadius);

        // Line-of-sight to the player (green = clear, red = blocked by a wall).
        if (Application.isPlaying && playerTransform != null)
        {
            Vector3 eye = origin + (Vector3)eyeOffset;
            Gizmos.color = HasLineOfSight() ? Color.green : Color.red;
            Gizmos.DrawLine(eye, playerTransform.position);
        }
    }
}
