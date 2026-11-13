// 纯 CPU 布料适配层：链接 NVIDIA 求解器，无需引擎运行时。
#include <PxPhysicsAPI.h>
#include <extensions/PxClothFabricCooker.h>
#include <algorithm>
#include <cmath>
#include <memory>
#include <mutex>
#include <stdexcept>
#include <string>
#include <vector>

using namespace physx;
static_assert(PX_PHYSICS_VERSION_MAJOR == 3 && PX_PHYSICS_VERSION_MINOR == 4 && PX_PHYSICS_VERSION_BUGFIX == 2,
              "Cloth parity requires PhysX 3.4.2");
#define API extern "C" __declspec(dllexport)

namespace
{
    thread_local std::string error;

    template <class F> int call(F f)
    {
        try
        {
            error.clear();
            return f();
        }
        catch (const std::exception &e)
        {
            error = e.what();
            return -1;
        }
        catch (...)
        {
            error = "Unknown native cloth exception";
            return -1;
        }
    }

    void finite(const float *p, int size)
    {
        if (!p || size < 0)
            throw std::invalid_argument("Invalid cloth numeric span");
        for (int i = 0; i < size; i++)
            if (!std::isfinite(p[i]))
                throw std::invalid_argument("Non-finite cloth value");
    }

    PxTransform pose(const float *p)
    {
        finite(p, 7);
        PxQuat q(p[3], p[4], p[5], p[6]);
        if (q.magnitudeSquared() < .99f || q.magnitudeSquared() > 1.01f)
            throw std::invalid_argument("Invalid cloth rotation");
        return PxTransform(PxVec3(p[0], p[1], p[2]), q.getNormalized());
    }

    struct Errors : PxErrorCallback
    {
        void reportError(PxErrorCode::Enum, const char *message, const char *, int) override
        {
            error = message;
        }
    };

    struct Runtime
    {
        PxDefaultAllocator allocator;
        Errors errors;
        PxFoundation *foundation = nullptr;
        PxPhysics *physics = nullptr;

        Runtime()
        {
            foundation = PxCreateFoundation(PX_FOUNDATION_VERSION, allocator, errors);
            if (!foundation)
                throw std::runtime_error("PhysX foundation creation failed: " + error);
            PxTolerancesScale scale;
            scale.length = 10;
            scale.speed = 100;
            physics = PxCreatePhysics(PX_PHYSICS_VERSION, *foundation, scale, false, nullptr);
            if (!physics)
            {
                foundation->release();
                throw std::runtime_error("PhysX creation failed");
            }
        }

        ~Runtime()
        {
            physics->release();
            foundation->release();
        }
    };

    std::mutex runtimeMutex;
    std::weak_ptr<Runtime> sharedRuntime;

    std::shared_ptr<Runtime> runtime()
    {
        std::lock_guard<std::mutex> lock(runtimeMutex);
        auto value = sharedRuntime.lock();
        if (!value)
        {
            value = std::make_shared<Runtime>();
            sharedRuntime = value;
        }
        return value;
    }

    struct Cloth
    {
        PxCloth *actor = nullptr;
        PxClothFabric *fabric = nullptr;
        std::vector<float> inverseMass, maxDistance;

        ~Cloth()
        {
            if (actor)
                actor->release();
            if (fabric)
                fabric->release();
        }
    };

    struct Scene
    {
        std::shared_ptr<Runtime> runtime;
        PxDefaultCpuDispatcher *dispatcher = nullptr;
        PxScene *scene = nullptr;
        std::vector<std::unique_ptr<Cloth>> cloths;

        Scene() : runtime(::runtime())
        {
            PxSceneDesc desc(runtime->physics->getTolerancesScale());
            desc.gravity = PxVec3(0, -98.1f, 0);
            dispatcher = PxDefaultCpuDispatcherCreate(0);
            desc.cpuDispatcher = dispatcher;
            desc.filterShader = PxDefaultSimulationFilterShader;
            scene = runtime->physics->createScene(desc);
            if (!scene)
            {
                dispatcher->release();
                throw std::runtime_error("PhysX cloth scene creation failed");
            }
        }

        ~Scene()
        {
            cloths.clear();
            scene->release();
            dispatcher->release();
        }
    };

    Scene &context(void *p)
    {
        if (!p)
            throw std::invalid_argument("Null cloth context");
        return *static_cast<Scene *>(p);
    }

    Cloth &cloth(Scene &s, int index)
    {
        if (index < 0 || static_cast<size_t>(index) >= s.cloths.size())
            throw std::out_of_range("Cloth index");
        return *s.cloths[index];
    }

    void constraints(Cloth &c, const float *positions, const float *normals, const float *separation)
    {
        const int size = static_cast<int>(c.inverseMass.size());
        finite(positions, size * 3);
        std::vector<PxClothParticleMotionConstraint> motion;
        motion.reserve(size);
        for (int i = 0; i < size; i++)
            motion.emplace_back(PxVec3(positions[i * 3], positions[i * 3 + 1], positions[i * 3 + 2]), c.maxDistance[i]);
        c.actor->setMotionConstraints(motion.data());
        if (normals && separation)
        {
            finite(normals, size * 3);
            finite(separation, size);
            std::vector<PxClothParticleSeparationConstraint> constraints;
            constraints.reserve(size);
            bool any = false;
            for (int i = 0; i < size; i++)
            {
                float radius = separation[i] >= PX_MAX_F32 * .5f ? 0 : std::abs(separation[i]);
                PxVec3 n(normals[i * 3], normals[i * 3 + 1], normals[i * 3 + 2]);
                n.normalize();
                PxVec3 position(positions[i * 3], positions[i * 3 + 1], positions[i * 3 + 2]);
                constraints.emplace_back(position - n * separation[i], radius);
                any |= radius > 0;
            }
            if (any)
                c.actor->setSeparationConstraints(constraints.data());
        }
    }
}

API const char *hs2_cloth_error()
{
    return error.c_str();
}

API unsigned hs2_cloth_version()
{
    return PX_PHYSICS_VERSION;
}

API void *hs2_cloth_create()
{
    try
    {
        error.clear();
        return new Scene();
    }
    catch (const std::exception &e)
    {
        error = e.what();
        return nullptr;
    }
}

API void hs2_cloth_destroy(void *p)
{
    delete static_cast<Scene *>(p);
}

static int addCloth(void *p, int count, const float *vertices, int indexCount, const unsigned *indices,
                    const float *maxDistances, const float *parameters, const float *transform, bool geodesic)
{
    return call(
        [&]
        {
            auto &s = context(p);
            if (count < 3 || count > 1000000 || indexCount < 3 || indexCount % 3 || !indices)
                throw std::invalid_argument("Invalid cloth topology");
            finite(vertices, count * 3);
            finite(maxDistances, count);
            finite(parameters, 15);
            auto value = std::make_unique<Cloth>();
            value->inverseMass.resize(count);
            value->maxDistance.assign(maxDistances, maxDistances + count);
            std::vector<PxVec3> points;
            std::vector<PxClothParticle> particles;
            points.reserve(count);
            particles.reserve(count);
            for (int i = 0; i < count; i++)
            {
                if (maxDistances[i] < 0)
                    throw std::invalid_argument("Negative cloth constraint");
                value->inverseMass[i] = maxDistances[i] == 0 ? 0.f : 1.f;
                points.emplace_back(vertices[i * 3], vertices[i * 3 + 1], vertices[i * 3 + 2]);
                particles.emplace_back(points.back(), value->inverseMass[i]);
            }
            for (int i = 0; i < indexCount; i++)
                if (indices[i] >= static_cast<unsigned>(count))
                    throw std::out_of_range("Cloth triangle index");
            PxClothMeshDesc desc;
            desc.points.count = count;
            desc.points.stride = sizeof(PxVec3);
            desc.points.data = points.data();
            desc.invMasses.count = count;
            desc.invMasses.stride = sizeof(float);
            desc.invMasses.data = value->inverseMass.data();
            desc.triangles.count = indexCount / 3;
            desc.triangles.stride = 3 * sizeof(unsigned);
            desc.triangles.data = indices;
            value->fabric = PxClothFabricCreate(*s.runtime->physics, desc, PxVec3(0, -1, 0), geodesic);
            if (!value->fabric)
                throw std::runtime_error("Cloth fabric cooking failed: " + error);
            value->actor =
                s.runtime->physics->createCloth(pose(transform), *value->fabric, particles.data(), PxClothFlags());
            if (!value->actor)
                throw std::runtime_error("Cloth actor creation failed: " + error);
            auto &actor = *value->actor;
            actor.setStretchConfig(PxClothFabricPhaseType::eVERTICAL, PxClothStretchConfig(parameters[0]));
            actor.setStretchConfig(PxClothFabricPhaseType::eHORIZONTAL, PxClothStretchConfig(parameters[0]));
            actor.setStretchConfig(PxClothFabricPhaseType::eSHEARING, PxClothStretchConfig(parameters[0]));
            actor.setStretchConfig(PxClothFabricPhaseType::eBENDING, PxClothStretchConfig(parameters[1]));
            actor.setDampingCoefficient(PxVec3(parameters[2]));
            actor.setFrictionCoefficient(parameters[3]);
            actor.setCollisionMassScale(parameters[4]);
            actor.setClothFlag(PxClothFlag::eSWEPT_CONTACT, parameters[5] != 0);
            actor.setSolverFrequency(parameters[6]);
            actor.setStiffnessFrequency(parameters[7]);
            actor.setTetherConfig(PxClothTetherConfig(parameters[8] != 0 ? 1.f : 0.f));
            actor.setSleepLinearVelocity(parameters[9]);
            actor.setSelfCollisionDistance(parameters[10]);
            actor.setSelfCollisionStiffness(parameters[11]);
            actor.setDragCoefficient(1 - parameters[12]);
            actor.setInertiaScale(parameters[13]);
            actor.setActorFlag(PxActorFlag::eDISABLE_GRAVITY, parameters[14] == 0);
            constraints(*value, vertices, nullptr, nullptr);
            s.scene->addActor(actor);
            s.cloths.push_back(std::move(value));
            return static_cast<int>(s.cloths.size() - 1);
        });
}

API int hs2_cloth_add(void *p, int count, const float *vertices, int indexCount, const unsigned *indices,
                      const float *maxDistances, const float *parameters, const float *transform)
{
    return addCloth(p, count, vertices, indexCount, indices, maxDistances, parameters, transform, true);
}

API int hs2_cloth_add_cooking(void *p, int count, const float *vertices, int indexCount, const unsigned *indices,
                              const float *maxDistances, const float *parameters, const float *transform, int geodesic)
{
    return addCloth(p, count, vertices, indexCount, indices, maxDistances, parameters, transform, geodesic != 0);
}

API int hs2_cloth_update(void *p, int index, const float *positions, const float *normals, const float *separation,
                         const float *transform, const float *acceleration, int reset)
{
    return call(
        [&]
        {
            auto &c = cloth(context(p), index);
            finite(acceleration, 3);
            constraints(c, positions, normals, separation);
            auto t = pose(transform);
            if (reset)
            {
                c.actor->setGlobalPose(t);
                std::vector<PxClothParticle> particles;
                for (size_t i = 0; i < c.inverseMass.size(); i++)
                    particles.emplace_back(PxVec3(positions[i * 3], positions[i * 3 + 1], positions[i * 3 + 2]),
                                           c.inverseMass[i]);
                c.actor->setParticles(particles.data(), particles.data());
                c.actor->clearInterpolation();
            }
            else
                c.actor->setTargetPose(t);
            c.actor->setExternalAcceleration(PxVec3(acceleration[0], acceleration[1], acceleration[2]));
            return 0;
        });
}

API int hs2_cloth_particles(void *p, int index, int selfCount, const unsigned *self, int virtualCount,
                            const unsigned *indices, int weightCount, const float *weights)
{
    return call(
        [&]
        {
            auto &c = cloth(context(p), index);
            if (selfCount < 0 || virtualCount < 0 || weightCount < 0)
                throw std::invalid_argument("Cloth particle counts");
            c.actor->setSelfCollisionIndices(self, selfCount);
            if (virtualCount)
            {
                finite(weights, weightCount * 3);
                c.actor->setVirtualParticles(virtualCount, indices, weightCount,
                                             reinterpret_cast<const PxVec3 *>(weights));
            }
            return 0;
        });
}

API int hs2_cloth_step(void *p, float seconds)
{
    return call(
        [&]
        {
            auto &s = context(p);
            if (!std::isfinite(seconds) || seconds <= 0 || seconds > .333334f)
                throw std::invalid_argument("Invalid cloth step");
            s.scene->simulate(seconds);
            if (!s.scene->fetchResults(true))
                throw std::runtime_error("Cloth simulation fetch failed");
            return 0;
        });
}

API int hs2_cloth_read(void *p, int index, int count, float *positions)
{
    return call(
        [&]
        {
            auto &c = cloth(context(p), index);
            if (count != static_cast<int>(c.inverseMass.size()) || !positions)
                throw std::invalid_argument("Cloth output span");
            auto *data = c.actor->lockParticleData();
            if (!data)
                throw std::runtime_error("Cloth particle data unavailable");
            for (int i = 0; i < count; i++)
            {
                const auto &v = data->particles[i].pos;
                positions[i * 3] = v.x;
                positions[i * 3 + 1] = v.y;
                positions[i * 3 + 2] = v.z;
            }
            data->unlock();
            return 0;
        });
}
