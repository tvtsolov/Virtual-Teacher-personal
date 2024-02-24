using System.ComponentModel.Design;
using VirtualTeacher.Exceptions;
using VirtualTeacher.Models;
using VirtualTeacher.Models.DTOs.Course;
using VirtualTeacher.Models.Enums;
using VirtualTeacher.Models.QueryParameters;
using VirtualTeacher.Repositories;
using VirtualTeacher.Repositories.Contracts;
using VirtualTeacher.Services.Contracts;
using VirtualTeacher.ViewModels;

namespace VirtualTeacher.Services;

public class CourseService : ICourseService
{
    private readonly ICourseRepository courseRepository;

    public CourseService(ICourseRepository courseRepository)
    {
        this.courseRepository = courseRepository;
    }

    private readonly IAccountService accountService;
    private readonly IUserService userService;
    private readonly IWebHostEnvironment hostEnvironment;
    private readonly IEmailService emailService;

    public CourseService(ICourseRepository courseRepository, IAccountService accountService, IUserService userService, IWebHostEnvironment hostEnvironment, IEmailService emailService)
    {
        this.courseRepository = courseRepository;
        this.accountService = accountService;
        this.userService = userService;
        this.hostEnvironment = hostEnvironment;
        this.emailService = emailService;
    }

    public List<Course> GetAllCourses()
    {
        CourseQueryParameters parameters = new CourseQueryParameters();
        parameters.PageSize = int.MaxValue;
        
        return courseRepository.FilterBy(parameters);
    }
    public PaginatedList<Course> FilterCoursesBy(CourseQueryParameters parameters)
    {
        User loggedUser = new User();
        if (accountService.UserIsLoggedIn())
        {
            loggedUser = accountService.GetLoggedUser();
        }
        else
        {
            loggedUser.UserRole = UserRole.Anonymous;
        }
        if (parameters.SeeDrafts != false && (loggedUser.UserRole != UserRole.Admin && loggedUser.UserRole != UserRole.Teacher))
        {
            throw new UnauthorizedAccessException("You are not authorized");
        }

        parameters.LoggedUserRole = loggedUser.UserRole;

        return courseRepository.FilterBy(parameters);
    }


    public List<Course> FilterByTeacherId(int? teacherId)
    {
        return courseRepository.FilterByTeacherId(teacherId);
    }
    public Course GetCourseById(int id)
    {
        var foundCourse = courseRepository.GetCourseById(id);

        return foundCourse ?? throw new EntityNotFoundException($"Course with id '{id}' was not found.");
    }

    public Course CreateCourse(CourseCreateDto dto)
    {

        var loggedUser = accountService.GetLoggedUser();

        if (string.IsNullOrEmpty(dto.VideoLink) == false)
        {
            dto.VideoLink = ConvertLink(dto.VideoLink);
        }

        var createdCourse = courseRepository.CreateCourse(dto, loggedUser);

        return createdCourse ?? throw new Exception($"The course could not be created.");
    }

    public Course UpdateCourse(int id, CourseUpdateDto dto)
    {
        var foundCourse = GetCourseById(id);
        var loggedUser = accountService.GetLoggedUser();

        if (loggedUser.UserRole != UserRole.Admin && foundCourse.ActiveTeachers.All(t => t != loggedUser))
        {
            throw new UnauthorizedOperationException($"A course can be updated only by its authors or an admin.");
        }

        if (string.IsNullOrEmpty(dto.VideoLink) == false && dto.VideoLink != foundCourse.VideoLink)
        {
            dto.VideoLink = ConvertLink(dto.VideoLink);
        }

        var updatedCourse = courseRepository.UpdateCourse(id, dto);

        return updatedCourse ?? throw new Exception($"The course could not be updated.");

    }

    public string DeleteCourse(int id)
    {
        var foundCourse = GetCourseById(id);
        var loggedUser = accountService.GetLoggedUser();

        if (loggedUser.UserRole != UserRole.Admin && foundCourse.ActiveTeachers.All(t => t != loggedUser))
        {
            throw new UnauthorizedOperationException($"A course can be deleted only by its authors or an admin.");
        }

        bool? courseDeleted = courseRepository.DeleteCourse(id);

        if (courseDeleted == true)
        {
            return $"Course with id '{id}' was deleted.";
        }

        if (courseDeleted == null)
        {
            throw new EntityNotFoundException($"Course with id '{id}' was not found.");
        }

        throw new Exception($"Course with id '{id}' could not be deleted.");
    }


    //Home VM methods
    public List<Course> GetNewestCourses()
    {
        return courseRepository.GetNewestCourses();
    }

    public List<Course> GetTopRatedCourses()
    {
        return courseRepository.GetTopRatedCourses();
    }

    public List<Course> GetPopularCourses()
    {
        return courseRepository.GetPopularCourses();
    }


    //Course enroll
    public string Enroll(int courseId)
    {
        var loggedUser = accountService.GetLoggedUser();
        var course = GetCourseById(courseId);

        if (course.EnrolledStudents.Any(u => u.Id == loggedUser.Id))
            throw new DuplicateEntityException("You are already enrolled in this course.");

        if (course.ActiveTeachers.Any(u => u.Id == loggedUser.Id))
            throw new DuplicateEntityException("You are already an active teacher in this course.");

        bool? enrollStatus = courseRepository.Enroll(courseId, loggedUser);

        if (enrollStatus == true)
        {
            emailService.EnrollConfirmation(loggedUser, course);
            return $"Successfully enrolled to course '{course.Title}'.";
        }

        throw new Exception($"The enroll request could not be completed.");
    }

    //Add new active teacher
    public string AddTeacher(int courseId, int teacherId)
    {
        var loggedUser = accountService.GetLoggedUser();
        var course = GetCourseById(courseId);

        if (!course.ActiveTeachers.Any(t => t.Id == loggedUser.Id)
            && loggedUser.UserRole != UserRole.Admin)
            throw new UnauthorizedOperationException("Only active course teachers or admins can assign new teachers.");

        if (course.ActiveTeachers.Any(t => t.Id == teacherId))
            throw new DuplicateEntityException("User is already an active teacher in the course");

        var teacher = userService.GetById(teacherId);

        if (teacher.UserRole != UserRole.Teacher)
            throw new InvalidOperationException("Only teachers can be assigned to the list of active course teachers.");

        bool? additionStatus = courseRepository.AddTeacher(courseId, teacher);

        if (additionStatus == true)
        {
            emailService.TeacherAddition(teacher, course);
            return $"Successfully added teacher {teacher.Username} to course '{course.Title}'";
        }

        throw new Exception($"The addition request could not be completed.");
    }

    public string InviteFriend(int courseId, string email, string name)
    {
        var course = GetCourseById(courseId);
        var loggedUser = accountService.GetLoggedUser();

        if (course.EnrolledStudents.Any(s => s.Email == email) 
            || course.ActiveTeachers.Any(t => t.Email == email))
            throw new DuplicateEntityException("User is already enrolled in this course");

        emailService.InviteFriend(email, name, loggedUser, course);

        return "Invitation has been successfully sent.";
    }

    // Ratings
    public List<Rating> GetRatings(int courseId)
    {
        var course = GetCourseById(courseId);
        var ratingsList = courseRepository.GetRatings(course);

        if (ratingsList.Count == 0)
        {
            throw new EntityNotFoundException($"No ratings found for course with id '{courseId}'.");
        }

        return ratingsList;
    }

    public Rating RateCourse(int courseId, RatingCreateDto dto)
    {
        var course = GetCourseById(courseId);
        var loggedUser = accountService.GetLoggedUser();
        var ratings = courseRepository.GetRatings(course);

        var foundRating = ratings.FirstOrDefault(rating => rating.Student.Id == loggedUser.Id
                                                           && rating.Course.Id == courseId);

        if (foundRating == null)
        {
            var createdRating = courseRepository.CreateRating(course, loggedUser, dto);
            return createdRating ?? throw new Exception($"The rating could not be created.");
        }

        var updatedRating = courseRepository.UpdateRating(foundRating, dto);
        return updatedRating ?? throw new Exception($"The rating could not be updated.");
    }

    public string RemoveRating(int courseId)
    {
        var course = GetCourseById(courseId);
        var loggedUser = accountService.GetLoggedUser();
        var ratings = courseRepository.GetRatings(course);

        var foundRating = ratings.FirstOrDefault(rating => rating.Student.Id == loggedUser.Id
                                                           && rating.Course.Id == courseId);

        if (foundRating == null)
        {
            return "Rating was not found.";
        }

        var ratingRemoved = courseRepository.RemoveRating(foundRating);

        return ratingRemoved ? "Rating was removed." : "Rating could not be removed.";
    }

    //Lectures
    public List<Lecture> GetLectures(int courseId)
    {
        var loggedUser = accountService.GetLoggedUser();
        var course = GetCourseById(courseId);

        ValidateUserEnrolledOrAdmin(course, loggedUser);

        List<Lecture> lectures = new List<Lecture>();
        lectures = courseRepository.GetLectures(course);
        if (lectures.Count == 0)
        {
            throw new EntityNotFoundException($"No lectures found for course with id '{courseId}'.");
        }

        return lectures;
    }

    public Lecture GetLectureById(int courseId, int lectureId)
    {
        var loggedUser = accountService.GetLoggedUser();
        var course = GetCourseById(courseId);

        ValidateUserEnrolledOrAdmin(course, loggedUser);

        var foundLecture = courseRepository.GetLecture(courseId, lectureId);

        return foundLecture ?? throw new EntityNotFoundException($"Lecture with id '{lectureId}' was not found.");
    }

    public Lecture UpdateLecture(LectureUpdateDto dto, int courseId, int lectureId)
    {
        var course = GetCourseById(courseId);
        var lecture = GetLectureById(courseId, lectureId);
        var loggedUser = accountService.GetLoggedUser();

        if (loggedUser.UserRole != UserRole.Admin
            && loggedUser.Id != lecture.TeacherId
            && loggedUser != course.ActiveTeachers.FirstOrDefault(at => at.Id == loggedUser.Id))
        {
            throw new UnauthorizedAccessException($"A lecture can be updated only by Teachers in the same Course or an Admin.");
        }

        dto.VideoLink = ConvertLink(dto.VideoLink);

        // todo to allow the co-teachers to be able to edit the lecture of the other teachers in the

        return courseRepository.UpdateLecture(lecture, dto);
    }

    public Lecture CreateLecture(LectureCreateDto dto, int courseId)
    {
        var loggedUser = accountService.GetLoggedUser();

        var course = GetCourseById(courseId);

        if (course == null)
        {
            throw new EntityNotFoundException($"Course with id {courseId} not found");
        }
        //check if the user has created the course
        if (loggedUser.UserRole != UserRole.Admin && !course.ActiveTeachers.Any(c => c.Username == loggedUser.Username))
            throw new UnauthorizedOperationException($"A lecture can be created only by the Course creator or an Admin.");

        dto.VideoLink = ConvertLink(dto.VideoLink);
        Lecture createdLecture = courseRepository.CreateLecture(dto, loggedUser, courseId);

        return createdLecture!;
    }

    public string DeleteLecture(int courseId, int lectureId)
    {
        Lecture lectureToDelete = GetLectureById(courseId, lectureId);
        string lectureTitle = lectureToDelete.Title;

        var course = GetCourseById(courseId);
        var loggedUser = accountService.GetLoggedUser();

        if (loggedUser.UserRole != UserRole.Admin
           && loggedUser.Id != lectureToDelete.TeacherId
           && loggedUser != course.ActiveTeachers.FirstOrDefault(at => at.Id == loggedUser.Id))
        {
            throw new UnauthorizedAccessException($"A lecture can be deleted only by Teachers in the same Course or an Admin.");
        }



        bool lectureDeleted = courseRepository.DeleteLecture(lectureToDelete);

        if (lectureDeleted)
        {
            return $"Lecture \"{lectureTitle}\" was successfully deleted from Course {course.Title}";
        }
        else
        {
            return "The Lecture was not deleted. It may have been null";
        }
    }


    //Comments
    public List<Comment> GetComments(int courseId, int lectureId)
    {
        var course = GetCourseById(courseId);
        var loggedUser = accountService.GetLoggedUser();

        ValidateUserEnrolledOrAdmin(course, loggedUser);

        var lecture = GetLectureById(courseId, lectureId);
        var commentList = courseRepository.GetComments(lecture);

        if (commentList.Count == 0)
            throw new EntityNotFoundException($"No comments found for lecture with id '{lectureId}'.");

        return commentList;
    }

    public List<Comment> GetCommentsByUser()
    {
        var loggedUser = accountService.GetLoggedUser();

        if (loggedUser == null)
        {
            throw new UnauthorizedAccessException();
        }

        var commentList = courseRepository.GetCommentsByUser(loggedUser);

        return commentList;
    }

    public Comment CreateComment(int courseId, int lectureId, CommentCreateDto dto)
    {
        var course = GetCourseById(courseId);
        var loggedUser = accountService.GetLoggedUser();

        ValidateUserEnrolledOrAdmin(course, loggedUser);
        var lecture = GetLectureById(courseId, lectureId);

        var createdComment = courseRepository.CreateComment(lecture, loggedUser, dto);

        if (string.IsNullOrEmpty(dto.Content))
            throw new InvalidUserInputException("The content of the comment cannot be empty");

        return createdComment!;
    }

    public Comment GetCommentById(int courseId, int lectureId, int commentId)
    {
        var course = courseRepository.GetCourseById(courseId); // checks for exception for the course
        var loggedUser = accountService.GetLoggedUser();

        ValidateUserEnrolledOrAdmin(course, loggedUser);
        var comment = courseRepository.GetComment(lectureId, commentId);

        return comment ?? throw new EntityNotFoundException($"Comment with id '{commentId}' was not found.");
    }

    public Comment UpdateComment(int courseId, int lectureId, int commentId, CommentCreateDto dto)
    {
        var comment = GetCommentById(courseId, lectureId, commentId);
        var loggedUser = accountService.GetLoggedUser();

        if (loggedUser.UserRole != UserRole.Admin && loggedUser.Id != comment.AuthorId)
            throw new UnauthorizedOperationException($"A comment can be updated only by its author or an admin.");

        var updatedComment = courseRepository.UpdateComment(comment, dto);

        return updatedComment ?? throw new Exception($"The comment could not be updated.");
    }

    public string DeleteComment(int courseId, int lectureId, int commentId)
    {
        var comment = GetCommentById(courseId, lectureId, commentId);
        var loggedUser = accountService.GetLoggedUser();

        if (loggedUser.UserRole != UserRole.Admin && loggedUser.Id != comment.AuthorId)
            throw new UnauthorizedOperationException($"A comment can be deleted only by its author or an admin.");

        bool? commentDeleted = courseRepository.DeleteComment(comment);

        if (commentDeleted == true)
            return $"Comment with id '{commentId}' was deleted.";

        throw new Exception($"Comment with id '{commentId}' could not be deleted.");
    }

    //Notes

    public string GetNote(int courseId, int lectureId)
    {
        var loggedUser = accountService.GetLoggedUser();
        _ = GetLectureById(courseId, lectureId); // checks if the lecture exists


        return courseRepository.GetNoteContent(loggedUser.Id, lectureId);
    }

    public string UpdateNoteContent(int courseId, int lectureId, string updatedContent)
    {
        var loggedUser = accountService.GetLoggedUser();
        _ = GetLectureById(courseId, lectureId); // checks if the lecture exists

        return courseRepository.UpdateNoteContent(loggedUser.Id, lectureId, updatedContent);
    }

    public string GetAssignmentFilePath(int courseId, int lectureId)
    {
        _ = GetCourseById(courseId);
        var lecture = GetLectureById(courseId, lectureId);

        if (string.IsNullOrEmpty(lecture.AssignmentLink))
        {
            throw new EntityNotFoundException("Assignment doesn't exists.");
        }

        if (!File.Exists(lecture.AssignmentLink))
        {
            throw new EntityNotFoundException("Assignment file not found.");
        }

        return lecture.AssignmentLink;
    }

    public string CreateAssignment(int courseId, int lectureId, IFormFile file)
    {
        _ = GetCourseById(courseId);
        var lecture = GetLectureById(courseId, lectureId);

        var allowedExtensions = new List<string> { ".txt", ".doc", ".docx", ".rtf" };
        var fileExtension = Path.GetExtension(file.FileName).ToLower();

        if (!allowedExtensions.Contains(fileExtension))
        {
            throw new ArgumentException("File type is not allowed.");
        }

        var privateRoot = Path.Combine(Directory.GetCurrentDirectory(), "PrivateData");
        var assignmentDirectory = Path.Combine(privateRoot, "Assignments", "course-" + courseId);

        if (!Directory.Exists(assignmentDirectory))
        {
            Directory.CreateDirectory(assignmentDirectory);
        }

        var fileNameWithoutExtension = "lecture-" + lectureId;
        var existingFiles = Directory.GetFiles(assignmentDirectory, fileNameWithoutExtension + ".*");

        foreach (var existingFile in existingFiles)
        {
            File.Delete(existingFile);
        }

        var fullPath = Path.Combine(assignmentDirectory, fileNameWithoutExtension + fileExtension);

        using var stream = new FileStream(fullPath, FileMode.Create);
        file.CopyTo(stream);

        var assignmentCreated = courseRepository.CreateAssignment(courseId, lectureId, fullPath);

        if (!assignmentCreated)
        {
            throw new Exception("Assignment could not be created.");
        }

        lecture.AssignmentLink = fullPath;
        return "File uploaded successfully.";
    }

    public string DeleteAssignment(int courseId, int lectureId)
    {
        _ = GetCourseById(courseId);
        var lecture = GetLectureById(courseId, lectureId); //todo ideally we can have a method for all that logic

        var privateRoot = Path.Combine(Directory.GetCurrentDirectory(), "PrivateData");
        var assignmentDirectory = Path.Combine(privateRoot, "Assignments", "course-" + courseId);

        var fileNameWithoutExtension = "lecture-" + lectureId;
        var existingFiles = Directory.GetFiles(assignmentDirectory, fileNameWithoutExtension + ".*");

        if (existingFiles.Length == 0)
        {
            throw new EntityNotFoundException("No assignment found for this lecture.");
        }

        foreach (var existingFile in existingFiles)
        {
            File.Delete(existingFile);
        }

        var assignmentDeleted = courseRepository.DeleteAssignment(courseId, lectureId);

        if (!assignmentDeleted)
        {
            throw new Exception("Assignment could not be deleted.");
        }

        courseRepository.DeleteAllSubmissions(lectureId);

        return "Assignment file deleted successfully.";
    }


    public string GetSubmissionFilePath(int courseId, int lectureId)
    {
        _ = GetCourseById(courseId);
        _ = GetLectureById(courseId, lectureId);

        var user = accountService.GetLoggedUser();

        var privateRoot = Path.Combine(Directory.GetCurrentDirectory(), "PrivateData");
        var submissionDirectory = Path.Combine(privateRoot, "Submissions", "course-" + courseId, "lecture-" + lectureId);

        var existingFiles = Directory.GetFiles(submissionDirectory, user.Username + ".*");
        if (existingFiles.Length == 0)
        {
            throw new FileNotFoundException("No submission file found for this user.");
        }

        return existingFiles[0];
    }

    public string GetSubmissionFilePath(int courseId, int lectureId, int studentId)
    {
        _ = GetCourseById(courseId);
        _ = GetLectureById(courseId, lectureId);

        var user = userService.GetById(studentId);

        var privateRoot = Path.Combine(Directory.GetCurrentDirectory(), "PrivateData");
        var submissionDirectory = Path.Combine(privateRoot, "Submissions", "course-" + courseId, "lecture-" + lectureId);

        var existingFiles = Directory.GetFiles(submissionDirectory, user.Username + ".*");
        if (existingFiles.Length == 0)
        {
            throw new FileNotFoundException("No submission file found for this user.");
        }

        return existingFiles[0];
    }

    public Submission? GetSubmission(int courseId, int lectureId, int userId)
    {
        _ = GetCourseById(courseId);
        _ = GetLectureById(courseId, lectureId);

        var submission = courseRepository.GetSubmission(lectureId, userId);

        return submission ?? null;
    }

    public string CreateSubmission(int courseId, int lectureId, IFormFile file)
    {
        _ = GetCourseById(courseId);
        _ = GetLectureById(courseId, lectureId);

        var user = accountService.GetLoggedUser();
        var allowedExtensions = new List<string> { ".txt", ".doc", ".docx", ".rtf" };
        var fileExtension = Path.GetExtension(file.FileName).ToLower();

        if (!allowedExtensions.Contains(fileExtension))
        {
            throw new ArgumentException("File type is not allowed.");
        }

        var privateRoot = Path.Combine(Directory.GetCurrentDirectory(), "PrivateData");
        var submissionDirectory = Path.Combine(privateRoot, "Submissions", "course-" + courseId, "lecture-" + lectureId);

        if (!Directory.Exists(submissionDirectory))
        {
            Directory.CreateDirectory(submissionDirectory);
        }

        var fileNameWithoutExtension = user.Username;
        var existingFiles = Directory.GetFiles(submissionDirectory, fileNameWithoutExtension + ".*");

        foreach (var existingFile in existingFiles)
        {
            File.Delete(existingFile);
        }

        var fullPath = Path.Combine(submissionDirectory, fileNameWithoutExtension + fileExtension);

        using var stream = new FileStream(fullPath, FileMode.Create);
        file.CopyTo(stream);

        // create submission class
        var submissionCreated = courseRepository.CreateSubmission(lectureId, user.Id);

        if (!submissionCreated)
        {
            throw new Exception("The submission could not be created.");
        }

        return "File uploaded successfully.";
    }

    public string DeleteSubmission(int courseId, int lectureId)
    {
        _ = GetCourseById(courseId);
        _ = GetLectureById(courseId, lectureId);

        var user = accountService.GetLoggedUser();

        var privateRoot = Path.Combine(Directory.GetCurrentDirectory(), "PrivateData");
        var submissionDirectory = Path.Combine(privateRoot, "Submissions", "course-" + courseId, "lecture-" + lectureId);

        var fileNameWithoutExtension = user.Username;
        var existingFiles = Directory.GetFiles(submissionDirectory, fileNameWithoutExtension + ".*");

        if (existingFiles.Length == 0)
        {
            throw new EntityNotFoundException("No submissions found for this lecture.");
        }

        foreach (var existingFile in existingFiles)
        {
            File.Delete(existingFile);
        }

        // delete submission class
        var submissionDeleted = courseRepository.DeleteSubmission(lectureId, user.Id);

        if (!submissionDeleted)
        {
            throw new Exception("The submission could not be deleted.");
        }

        return "Submission file deleted successfully.";
    }

    public string AssessSubmission(int lectureId, int userId, byte grade)
    {
        if(courseRepository.AssessSubmission(lectureId, userId, grade))
        {
            return $"Assessment successfull. New grade: {grade}";
        }
        else
        {
            throw new EntityNotFoundException($"Assessment failed. Submission was not found");
        }
    }


    // Validations
    private void ValidateUserEnrolledOrAdmin(Course course, User user)
    {
        if (!course.EnrolledStudents.Any(u => u.Id == user.Id)
        && !course.ActiveTeachers.Any(u => u.Id == user.Id)
        && user.UserRole != UserRole.Admin)
        {
            throw new UnauthorizedOperationException("You are not enrolled in this course.");
        }
    }

    //Converts base YT link to embed link
    public string ConvertLink(string link)
    {
        if (link.Contains("watch?v="))
        {
            link = link.Replace("watch?v=", "embed/");
        }

        return link;
    }

    public int GetCoursesCount()
    {
        return courseRepository.GetCoursesCount();
    }

    public int GetLecturesCount()
    {
        return courseRepository.GetLecturesCount();
    }
}